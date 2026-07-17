using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>
/// Embedded SQLite durable store (WAL mode, <c>SQLITE_BUSY</c> retry). Opens once at startup after a
/// mandatory integrity check (corrupt ⇒ fail-start, G4) and schema migration.
/// </summary>
/// <remarks>
/// Одне довговічне з'єднання, серіалізоване локом (Фаза 1 не має конкурентних записувачів — це
/// страхувальна коректність). <c>busy_timeout</c> + явний ретрай покривають <c>SQLITE_BUSY</c>
/// (наприклад під час backup/WAL-checkpoint). Часові мітки зберігаємо як ISO-8601 (round-trip).
/// </remarks>
public sealed class DurableStore : IDurableStore, IDisposable
{
    private const int BusyTimeoutMs = 5000;
    private const int MaxBusyRetries = 5;

    private readonly SqliteConnection _connection;
    private readonly ILogger<DurableStore> _logger;
    private readonly object _sync = new();

    private int _schemaVersion;
    private bool _initialized;

    /// <summary>Creates the store over <paramref name="databasePath"/> (not opened until <see cref="Initialize"/>).</summary>
    public DurableStore(string databasePath, ILogger<DurableStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        _connection = new SqliteConnection(connectionString);
    }

    /// <inheritdoc />
    public int SchemaVersion => _schemaVersion;

    /// <summary>
    /// Opens the connection, applies pragmas (WAL, busy-timeout, foreign keys), runs the integrity
    /// check (corrupt ⇒ throw, fail-start) and migrates the schema. Idempotent.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database failed the integrity check (corrupt).</exception>
    public void Initialize()
    {
        lock (_sync)
        {
            if (_initialized)
                return;

            _connection.Open();

            Execute("PRAGMA journal_mode=WAL;");
            Execute($"PRAGMA busy_timeout={BusyTimeoutMs};");
            Execute("PRAGMA synchronous=NORMAL;");
            Execute("PRAGMA foreign_keys=ON;");

            RunIntegrityCheck();
            _schemaVersion = SchemaMigrator.Migrate(_connection, _logger);
            _initialized = true;

            _logger.LogInformation("Durable-стор відкрито (WAL); user_version={Version}.", _schemaVersion);
        }
    }

    /// <inheritdoc />
    public void UpsertIdentity(IdentityRecord identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_sync)
        {
            WithRetry(() =>
            {
                using var transaction = _connection.BeginTransaction();

                using (var upsert = _connection.CreateCommand())
                {
                    upsert.Transaction = transaction;
                    // created_at ставиться лише при першій вставці; повторний upsert оновлює тільки role.
                    upsert.CommandText =
                        """
                        INSERT INTO identities (id, role, created_at)
                        VALUES ($id, $role, $createdAt)
                        ON CONFLICT(id) DO UPDATE SET role = excluded.role;
                        """;
                    upsert.Parameters.AddWithValue("$id", identity.Id);
                    upsert.Parameters.AddWithValue("$role", identity.Role);
                    upsert.Parameters.AddWithValue("$createdAt", FormatTimestamp(identity.CreatedAt));
                    upsert.ExecuteNonQuery();
                }

                using (var clear = _connection.CreateCommand())
                {
                    clear.Transaction = transaction;
                    clear.CommandText = "DELETE FROM identity_accounts WHERE identity_id = $id;";
                    clear.Parameters.AddWithValue("$id", identity.Id);
                    clear.ExecuteNonQuery();
                }

                foreach (var account in identity.LinkedAccounts.Where(a => !string.IsNullOrWhiteSpace(a)))
                {
                    using var link = _connection.CreateCommand();
                    link.Transaction = transaction;
                    link.CommandText =
                        "INSERT OR IGNORE INTO identity_accounts (identity_id, account) VALUES ($id, $account);";
                    link.Parameters.AddWithValue("$id", identity.Id);
                    link.Parameters.AddWithValue("$account", account);
                    link.ExecuteNonQuery();
                }

                transaction.Commit();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public IdentityRecord? GetIdentity(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                string role;
                DateTimeOffset createdAt;

                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "SELECT role, created_at FROM identities WHERE id = $id;";
                    command.Parameters.AddWithValue("$id", id);
                    using var reader = command.ExecuteReader();
                    if (!reader.Read())
                        return null;

                    role = reader.GetString(0);
                    createdAt = ParseTimestamp(reader.GetString(1));
                }

                return new IdentityRecord(id, role, ReadLinkedAccounts(id), createdAt);
            });
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IdentityRecord> GetIdentities()
    {
        lock (_sync)
        {
            return WithRetry(() =>
            {
                var ids = new List<(string Id, string Role, DateTimeOffset CreatedAt)>();
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "SELECT id, role, created_at FROM identities ORDER BY id;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        ids.Add((reader.GetString(0), reader.GetString(1), ParseTimestamp(reader.GetString(2))));
                }

                return (IReadOnlyList<IdentityRecord>)ids
                    .Select(row => new IdentityRecord(row.Id, row.Role, ReadLinkedAccounts(row.Id), row.CreatedAt))
                    .ToList();
            });
        }
    }

    /// <inheritdoc />
    public void UpsertAccountBinding(AccountBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        lock (_sync)
        {
            WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO account_bindings (account, owning_identity_id, is_private, created_at)
                    VALUES ($account, $identity, $private, $createdAt)
                    ON CONFLICT(account) DO UPDATE SET
                        owning_identity_id = excluded.owning_identity_id,
                        is_private         = excluded.is_private;
                    """;
                command.Parameters.AddWithValue("$account", binding.Account);
                command.Parameters.AddWithValue("$identity", binding.OwningIdentityId);
                command.Parameters.AddWithValue("$private", binding.IsPrivate ? 1 : 0);
                command.Parameters.AddWithValue("$createdAt", FormatTimestamp(binding.CreatedAt));
                command.ExecuteNonQuery();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public AccountBinding? GetAccountBinding(string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    "SELECT owning_identity_id, is_private, created_at FROM account_bindings WHERE account = $account;";
                command.Parameters.AddWithValue("$account", account);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    return null;

                return new AccountBinding(
                    account, reader.GetString(0), reader.GetInt32(1) != 0, ParseTimestamp(reader.GetString(2)));
            });
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AccountBinding> GetBindingsForIdentity(string identityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                var bindings = new List<AccountBinding>();
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT account, is_private, created_at
                    FROM account_bindings
                    WHERE owning_identity_id = $identity
                    ORDER BY account;
                    """;
                command.Parameters.AddWithValue("$identity", identityId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    bindings.Add(new AccountBinding(
                        reader.GetString(0), identityId, reader.GetInt32(1) != 0, ParseTimestamp(reader.GetString(2))));
                }

                return (IReadOnlyList<AccountBinding>)bindings;
            });
        }
    }

    /// <inheritdoc />
    public void InsertToken(TokenRecord token)
    {
        ArgumentNullException.ThrowIfNull(token);

        lock (_sync)
        {
            WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO auth_tokens
                        (token_hash, pepper_version, identity_id, expires_at, is_revoked, created_at)
                    VALUES ($hash, $ver, $identity, $expires, $revoked, $created);
                    """;
                BindToken(command, token);
                command.ExecuteNonQuery();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public TokenRecord? GetToken(string tokenHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT pepper_version, identity_id, expires_at, is_revoked, created_at
                    FROM auth_tokens WHERE token_hash = $hash;
                    """;
                command.Parameters.AddWithValue("$hash", tokenHash);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    return null;

                return new TokenRecord(
                    tokenHash,
                    reader.GetInt32(0),
                    reader.GetString(1),
                    ParseTimestamp(reader.GetString(2)),
                    reader.GetInt32(3) != 0,
                    ParseTimestamp(reader.GetString(4)));
            });
        }
    }

    /// <inheritdoc />
    public void RotateToken(string oldTokenHash, TokenRecord newToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldTokenHash);
        ArgumentNullException.ThrowIfNull(newToken);

        lock (_sync)
        {
            WithRetry(() =>
            {
                using var transaction = _connection.BeginTransaction();

                int revoked;
                using (var update = _connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE auth_tokens SET is_revoked = 1 WHERE token_hash = $old;";
                    update.Parameters.AddWithValue("$old", oldTokenHash);
                    revoked = update.ExecuteNonQuery();
                }

                // Ротація без попередника = логічна помилка: zero-overlap (W21) вимагає наявного старого
                // токена, який ми revoke-имо у тій самій транзакції, що й вставку нового.
                if (revoked == 0)
                {
                    throw new InvalidOperationException(
                        "Ротація токена без наявного попередника — старий token_hash не знайдено у сторі.");
                }

                using (var insert = _connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText =
                        """
                        INSERT INTO auth_tokens
                            (token_hash, pepper_version, identity_id, expires_at, is_revoked, created_at)
                        VALUES ($hash, $ver, $identity, $expires, $revoked, $created);
                        """;
                    BindToken(insert, newToken);
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public void RevokeIdentityCredentials(string identityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);

        lock (_sync)
        {
            WithRetry(() =>
            {
                using var transaction = _connection.BeginTransaction();

                // Каскад revoke — усі токени identity + device-секрет — в одній транзакції (task 1.4).
                using (var revokeTokens = _connection.CreateCommand())
                {
                    revokeTokens.Transaction = transaction;
                    revokeTokens.CommandText =
                        "UPDATE auth_tokens SET is_revoked = 1 WHERE identity_id = $identity;";
                    revokeTokens.Parameters.AddWithValue("$identity", identityId);
                    revokeTokens.ExecuteNonQuery();
                }

                using (var revokeSecret = _connection.CreateCommand())
                {
                    revokeSecret.Transaction = transaction;
                    revokeSecret.CommandText =
                        "UPDATE device_secrets SET is_revoked = 1 WHERE identity_id = $identity;";
                    revokeSecret.Parameters.AddWithValue("$identity", identityId);
                    revokeSecret.ExecuteNonQuery();
                }

                // Каскад на group-bindings (add-group-claim-receive, task 2.4): revoke identity гасить і
                // її (identity, group) binding-и у ТІЙ САМІЙ транзакції. group_bindings існує з міграції v6.
                using (var revokeBindings = _connection.CreateCommand())
                {
                    revokeBindings.Transaction = transaction;
                    revokeBindings.CommandText =
                        "UPDATE group_bindings SET revoked = 1 WHERE identity_id = $identity;";
                    revokeBindings.Parameters.AddWithValue("$identity", identityId);
                    revokeBindings.ExecuteNonQuery();
                }

                transaction.Commit();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public void UpsertDeviceSecret(DeviceSecretRecord secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        lock (_sync)
        {
            WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                // created_at ставиться лише при першій вставці (як у identities); re-enrollment ключа
                // оновлює public_key/algorithm/is_revoked.
                command.CommandText =
                    """
                    INSERT INTO device_secrets (identity_id, public_key, algorithm, is_revoked, created_at)
                    VALUES ($identity, $pubkey, $algo, $revoked, $created)
                    ON CONFLICT(identity_id) DO UPDATE SET
                        public_key = excluded.public_key,
                        algorithm  = excluded.algorithm,
                        is_revoked = excluded.is_revoked;
                    """;
                command.Parameters.AddWithValue("$identity", secret.IdentityId);
                command.Parameters.AddWithValue("$pubkey", secret.PublicKey);
                command.Parameters.AddWithValue("$algo", secret.Algorithm);
                command.Parameters.AddWithValue("$revoked", secret.IsRevoked ? 1 : 0);
                command.Parameters.AddWithValue("$created", FormatTimestamp(secret.CreatedAt));
                command.ExecuteNonQuery();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public DeviceSecretRecord? GetDeviceSecret(string identityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT public_key, algorithm, is_revoked, created_at
                    FROM device_secrets WHERE identity_id = $identity;
                    """;
                command.Parameters.AddWithValue("$identity", identityId);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    return null;

                return new DeviceSecretRecord(
                    identityId,
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2) != 0,
                    ParseTimestamp(reader.GetString(3)));
            });
        }
    }

    /// <inheritdoc />
    public int CountBudgetBlocks(string windowKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowKey);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*) FROM budget_reservations WHERE window_key = $window;";
                command.Parameters.AddWithValue("$window", windowKey);
                return Convert.ToInt32(command.ExecuteScalar());
            });
        }
    }

    /// <inheritdoc />
    public void InsertBudgetBlock(string windowKey, int blockIndex, int blockSize, DateTimeOffset reservedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowKey);

        lock (_sync)
        {
            WithRetry(() =>
            {
                // Прямий INSERT (не OR IGNORE): конфлікт PK (window_key, block_index) = double-reserve,
                // який МУСИТЬ кинути (SqliteException не є BUSY/LOCKED, тож WithRetry його не ковтає).
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO budget_reservations (window_key, block_index, block_size, reserved_at)
                    VALUES ($window, $index, $size, $reserved);
                    """;
                command.Parameters.AddWithValue("$window", windowKey);
                command.Parameters.AddWithValue("$index", blockIndex);
                command.Parameters.AddWithValue("$size", blockSize);
                command.Parameters.AddWithValue("$reserved", FormatTimestamp(reservedAt));
                command.ExecuteNonQuery();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public bool IsKnownRecipient(string identityId, string recipientHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientHash);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    "SELECT 1 FROM known_recipients WHERE identity_id = $id AND recipient_hash = $hash LIMIT 1;";
                command.Parameters.AddWithValue("$id", identityId);
                command.Parameters.AddWithValue("$hash", recipientHash);
                using var reader = command.ExecuteReader();
                return reader.Read();
            });
        }
    }

    /// <inheritdoc />
    public void UpsertKnownRecipient(string identityId, string recipientHash, DateTimeOffset firstSeen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientHash);

        lock (_sync)
        {
            WithRetry(() =>
            {
                // INSERT OR IGNORE: first_seen первинного контакту зберігається (повторний контакт — no-op).
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT OR IGNORE INTO known_recipients (identity_id, recipient_hash, first_seen)
                    VALUES ($id, $hash, $seen);
                    """;
                command.Parameters.AddWithValue("$id", identityId);
                command.Parameters.AddWithValue("$hash", recipientHash);
                command.Parameters.AddWithValue("$seen", FormatTimestamp(firstSeen));
                command.ExecuteNonQuery();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public void InsertInvite(InviteRecord invite)
    {
        ArgumentNullException.ThrowIfNull(invite);

        lock (_sync)
        {
            WithRetry(() =>
            {
                // Прямий INSERT (не OR IGNORE): дубль code_hash = помилка (SqliteException не є BUSY/LOCKED,
                // тож WithRetry його не ковтає — конфлікт спливає викликачу).
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO invites
                        (code_hash, created_by, consumed, attempt_count, max_attempts, expires_at, consumed_by, created_at)
                    VALUES ($hash, $createdBy, $consumed, $attempts, $maxAttempts, $expires, $consumedBy, $created);
                    """;
                command.Parameters.AddWithValue("$hash", invite.CodeHash);
                command.Parameters.AddWithValue("$createdBy", invite.CreatedBy);
                command.Parameters.AddWithValue("$consumed", invite.Consumed ? 1 : 0);
                command.Parameters.AddWithValue("$attempts", invite.AttemptCount);
                command.Parameters.AddWithValue("$maxAttempts", invite.MaxAttempts);
                command.Parameters.AddWithValue("$expires", FormatTimestamp(invite.ExpiresAt));
                command.Parameters.AddWithValue("$consumedBy", (object?)invite.ConsumedBy ?? DBNull.Value);
                command.Parameters.AddWithValue("$created", FormatTimestamp(invite.CreatedAt));
                command.ExecuteNonQuery();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public InviteRecord? GetInvite(string codeHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT created_by, consumed, attempt_count, max_attempts, expires_at, consumed_by, created_at
                    FROM invites WHERE code_hash = $hash;
                    """;
                command.Parameters.AddWithValue("$hash", codeHash);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    return null;

                return ReadInvite(codeHash, reader);
            });
        }
    }

    /// <inheritdoc />
    public bool TryConsumeInvite(string codeHash, string consumedByIdentityId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumedByIdentityId);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                using var transaction = _connection.BeginTransaction();

                // 1. Кожна спроба (зокрема невдала) інкрементує лічильник — G12: cap рахує всі guesses.
                using (var bump = _connection.CreateCommand())
                {
                    bump.Transaction = transaction;
                    bump.CommandText =
                        "UPDATE invites SET attempt_count = attempt_count + 1 WHERE code_hash = $hash;";
                    bump.Parameters.AddWithValue("$hash", codeHash);
                    bump.ExecuteNonQuery();
                }

                // 2. Перечитуємо стан ПІСЛЯ інкремента (у тій самій транзакції).
                InviteRecord? invite;
                using (var read = _connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText =
                        """
                        SELECT created_by, consumed, attempt_count, max_attempts, expires_at, consumed_by, created_at
                        FROM invites WHERE code_hash = $hash;
                        """;
                    read.Parameters.AddWithValue("$hash", codeHash);
                    using var reader = read.ExecuteReader();
                    invite = reader.Read() ? ReadInvite(codeHash, reader) : null;
                }

                // 3. Не redeemable? Транзакцію все одно комітимо — інкремент спроби має зберегтися (G12).
                //    (Невідомий code_hash → bump не змінив рядків, invite==null; коміт — no-op запису.)
                if (invite is null || invite.Consumed || invite.ExpiresAt <= now || invite.AttemptCount > invite.MaxAttempts)
                {
                    transaction.Commit();
                    return false;
                }

                // 4. Умовний consume: виграє лише той, хто перевів consumed 0→1 (rows-affected==1).
                int consumed;
                using (var claim = _connection.CreateCommand())
                {
                    claim.Transaction = transaction;
                    claim.CommandText =
                        """
                        UPDATE invites SET consumed = 1, consumed_by = $id
                        WHERE code_hash = $hash AND consumed = 0;
                        """;
                    claim.Parameters.AddWithValue("$id", consumedByIdentityId);
                    claim.Parameters.AddWithValue("$hash", codeHash);
                    consumed = claim.ExecuteNonQuery();
                }

                transaction.Commit();
                return consumed == 1;
            });
        }
    }

    /// <inheritdoc />
    public int AppendAbuseLog(AbuseLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO abuse_log (event_type, subject_hmac, salt, logged_at)
                    VALUES ($type, $hmac, $salt, $loggedAt);
                    SELECT last_insert_rowid();
                    """;
                command.Parameters.AddWithValue("$type", record.EventType);
                command.Parameters.AddWithValue("$hmac", record.SubjectHmac);
                command.Parameters.AddWithValue("$salt", record.Salt);
                command.Parameters.AddWithValue("$loggedAt", FormatTimestamp(record.LoggedAt));
                return Convert.ToInt32(command.ExecuteScalar());
            });
        }
    }

    /// <inheritdoc />
    public void UpsertAdminCredential(AdminCredentialRecord credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        lock (_sync)
        {
            WithRetry(() =>
            {
                // INSERT OR IGNORE: повторний provision того самого SPKI — no-op (зберігає revoked-прапорець
                // і created_at). Ідемпотентний bootstrap (task 2.2).
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT OR IGNORE INTO admin_credentials (spki_sha256, identity_id, revoked, created_at)
                    VALUES ($spki, $identity, $revoked, $created);
                    """;
                command.Parameters.AddWithValue("$spki", credential.SpkiSha256);
                command.Parameters.AddWithValue("$identity", credential.IdentityId);
                command.Parameters.AddWithValue("$revoked", credential.Revoked ? 1 : 0);
                command.Parameters.AddWithValue("$created", FormatTimestamp(credential.CreatedAt));
                command.ExecuteNonQuery();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public AdminCredentialRecord? GetAdminCredential(string spkiSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spkiSha256);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    "SELECT identity_id, revoked, created_at FROM admin_credentials WHERE spki_sha256 = $spki;";
                command.Parameters.AddWithValue("$spki", spkiSha256);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    return null;

                return new AdminCredentialRecord(
                    spkiSha256, reader.GetString(0), reader.GetInt32(1) != 0, ParseTimestamp(reader.GetString(2)));
            });
        }
    }

    /// <inheritdoc />
    public void RevokeAdminCredential(string spkiSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spkiSha256);

        lock (_sync)
        {
            WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "UPDATE admin_credentials SET revoked = 1 WHERE spki_sha256 = $spki;";
                command.Parameters.AddWithValue("$spki", spkiSha256);
                command.ExecuteNonQuery();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public void AppendAuditEntry(AuditLogRecord entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_sync)
        {
            WithRetry(() =>
            {
                // Прямий INSERT з явним seq: дубль seq = розрив ланцюга (SqliteException не є BUSY/LOCKED,
                // тож WithRetry його не ковтає — спливає викликачу). Серіалізацію appendʼів гарантує
                // AuditLogService (єдиний записувач під власним локом).
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO audit_log
                        (seq, event_type, actor_identity, detail_hash, prev_hash, entry_hash, logged_at)
                    VALUES ($seq, $type, $actor, $detail, $prev, $entry, $loggedAt);
                    """;
                command.Parameters.AddWithValue("$seq", entry.Seq);
                command.Parameters.AddWithValue("$type", entry.EventType);
                command.Parameters.AddWithValue("$actor", entry.ActorIdentity);
                command.Parameters.AddWithValue("$detail", entry.DetailHash);
                command.Parameters.AddWithValue("$prev", entry.PrevHash);
                command.Parameters.AddWithValue("$entry", entry.EntryHash);
                command.Parameters.AddWithValue("$loggedAt", FormatTimestamp(entry.LoggedAt));
                command.ExecuteNonQuery();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public AuditLogRecord? GetLastAuditEntry()
    {
        lock (_sync)
        {
            return WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT seq, event_type, actor_identity, detail_hash, prev_hash, entry_hash, logged_at
                    FROM audit_log ORDER BY seq DESC LIMIT 1;
                    """;
                using var reader = command.ExecuteReader();
                return reader.Read() ? ReadAuditEntry(reader) : null;
            });
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AuditLogRecord> GetAuditEntries()
    {
        lock (_sync)
        {
            return WithRetry(() =>
            {
                var entries = new List<AuditLogRecord>();
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT seq, event_type, actor_identity, detail_hash, prev_hash, entry_hash, logged_at
                    FROM audit_log ORDER BY seq ASC;
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    entries.Add(ReadAuditEntry(reader));
                return (IReadOnlyList<AuditLogRecord>)entries;
            });
        }
    }

    // Читає AuditLogRecord із рядка (порядок колонок: seq, event_type, actor_identity, detail_hash,
    // prev_hash, entry_hash, logged_at).
    private static AuditLogRecord ReadAuditEntry(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            ParseTimestamp(reader.GetString(6)));

    // Читає InviteRecord із рядка (порядок колонок: created_by, consumed, attempt_count, max_attempts,
    // expires_at, consumed_by, created_at). code_hash передаємо явно (він у WHERE, не у SELECT-списку).
    private static InviteRecord ReadInvite(string codeHash, SqliteDataReader reader) =>
        new(
            codeHash,
            reader.GetString(0),
            reader.GetInt32(1) != 0,
            reader.GetInt32(2),
            reader.GetInt32(3),
            ParseTimestamp(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            ParseTimestamp(reader.GetString(6)));

    /// <inheritdoc />
    public void InsertGroupClaim(GroupClaimRecord claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        lock (_sync)
        {
            WithRetry(() =>
            {
                // Прямий INSERT (не OR IGNORE): дубль code_hash = помилка (SqliteException не BUSY/LOCKED,
                // тож WithRetry його не ковтає — конфлікт спливає викликачу).
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO group_claims (code_hash, created_by, consumed, expires_at, created_at)
                    VALUES ($hash, $createdBy, $consumed, $expires, $created);
                    """;
                command.Parameters.AddWithValue("$hash", claim.CodeHash);
                command.Parameters.AddWithValue("$createdBy", claim.CreatedBy);
                command.Parameters.AddWithValue("$consumed", claim.Consumed ? 1 : 0);
                command.Parameters.AddWithValue("$expires", FormatTimestamp(claim.ExpiresAt));
                command.Parameters.AddWithValue("$created", FormatTimestamp(claim.CreatedAt));
                command.ExecuteNonQuery();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public GroupClaimRecord? GetGroupClaim(string codeHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    "SELECT created_by, consumed, expires_at, created_at FROM group_claims WHERE code_hash = $hash;";
                command.Parameters.AddWithValue("$hash", codeHash);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    return null;

                return new GroupClaimRecord(
                    codeHash,
                    reader.GetString(0),
                    reader.GetInt32(1) != 0,
                    ParseTimestamp(reader.GetString(2)),
                    ParseTimestamp(reader.GetString(3)));
            });
        }
    }

    /// <inheritdoc />
    public bool TryConsumeGroupClaim(string codeHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                using var transaction = _connection.BeginTransaction();

                // 1. Перечитуємо стан claim-у у транзакції.
                bool consumed;
                DateTimeOffset expiresAt;
                using (var read = _connection.CreateCommand())
                {
                    read.Transaction = transaction;
                    read.CommandText = "SELECT consumed, expires_at FROM group_claims WHERE code_hash = $hash;";
                    read.Parameters.AddWithValue("$hash", codeHash);
                    using var reader = read.ExecuteReader();
                    if (!reader.Read())
                    {
                        transaction.Commit();
                        return false; // невідомий code_hash
                    }

                    consumed = reader.GetInt32(0) != 0;
                    expiresAt = ParseTimestamp(reader.GetString(1));
                }

                // 2. Не redeemable (уже consumed / прострочений) → відмова.
                if (consumed || expiresAt <= now)
                {
                    transaction.Commit();
                    return false;
                }

                // 3. Умовний consume: виграє лише той, хто перевів consumed 0→1 (rows-affected==1, W20).
                int affected;
                using (var claim = _connection.CreateCommand())
                {
                    claim.Transaction = transaction;
                    claim.CommandText =
                        "UPDATE group_claims SET consumed = 1 WHERE code_hash = $hash AND consumed = 0;";
                    claim.Parameters.AddWithValue("$hash", codeHash);
                    affected = claim.ExecuteNonQuery();
                }

                transaction.Commit();
                return affected == 1;
            });
        }
    }

    /// <inheritdoc />
    public void InsertGroupBinding(GroupBindingRecord binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        lock (_sync)
        {
            WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO group_bindings
                        (binding_id, identity_id, group_id, anchor_aci, expires_at, revoked, created_at)
                    VALUES ($id, $identity, $group, $anchor, $expires, $revoked, $created);
                    """;
                command.Parameters.AddWithValue("$id", binding.BindingId);
                command.Parameters.AddWithValue("$identity", binding.IdentityId);
                command.Parameters.AddWithValue("$group", binding.GroupId);
                command.Parameters.AddWithValue("$anchor", binding.AnchorAci);
                command.Parameters.AddWithValue("$expires", FormatTimestamp(binding.ExpiresAt));
                command.Parameters.AddWithValue("$revoked", binding.Revoked ? 1 : 0);
                command.Parameters.AddWithValue("$created", FormatTimestamp(binding.CreatedAt));
                command.ExecuteNonQuery();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public GroupBindingRecord? GetGroupBinding(string identityId, string groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                // Найновіший binding для (identity, group) — re-claim додає новий рядок, беремо останній.
                command.CommandText =
                    """
                    SELECT binding_id, anchor_aci, expires_at, revoked, created_at
                    FROM group_bindings
                    WHERE identity_id = $identity AND group_id = $group
                    ORDER BY created_at DESC LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$identity", identityId);
                command.Parameters.AddWithValue("$group", groupId);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    return null;

                return new GroupBindingRecord(
                    reader.GetString(0), identityId, groupId, reader.GetString(1),
                    ParseTimestamp(reader.GetString(2)), reader.GetInt32(3) != 0, ParseTimestamp(reader.GetString(4)));
            });
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<GroupBindingRecord> GetGroupBindingsForIdentity(string identityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                var bindings = new List<GroupBindingRecord>();
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT binding_id, group_id, anchor_aci, expires_at, revoked, created_at
                    FROM group_bindings
                    WHERE identity_id = $identity
                    ORDER BY created_at DESC;
                    """;
                command.Parameters.AddWithValue("$identity", identityId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    bindings.Add(new GroupBindingRecord(
                        reader.GetString(0), identityId, reader.GetString(1), reader.GetString(2),
                        ParseTimestamp(reader.GetString(3)), reader.GetInt32(4) != 0, ParseTimestamp(reader.GetString(5))));
                }

                return (IReadOnlyList<GroupBindingRecord>)bindings;
            });
        }
    }

    /// <inheritdoc />
    public void RevokeGroupBinding(string bindingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingId);

        lock (_sync)
        {
            WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "UPDATE group_bindings SET revoked = 1 WHERE binding_id = $id;";
                command.Parameters.AddWithValue("$id", bindingId);
                command.ExecuteNonQuery();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public void RevokeGroupBindingsForIdentity(string identityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);

        lock (_sync)
        {
            WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "UPDATE group_bindings SET revoked = 1 WHERE identity_id = $identity;";
                command.Parameters.AddWithValue("$identity", identityId);
                command.ExecuteNonQuery();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public SendDedupRecord? GetSendDedup(string identityId, string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT status, result_json, budget_reserved, created_at
                    FROM send_dedup WHERE identity_id = $id AND message_id = $msg;
                    """;
                command.Parameters.AddWithValue("$id", identityId);
                command.Parameters.AddWithValue("$msg", messageId);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    return null;

                return new SendDedupRecord(
                    identityId,
                    messageId,
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt32(2) != 0,
                    ParseTimestamp(reader.GetString(3)));
            });
        }
    }

    /// <inheritdoc />
    public bool TryBeginSend(string identityId, string messageId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        lock (_sync)
        {
            return WithRetry(() =>
            {
                // Атомарний claim: INSERT OR IGNORE + changes(). Виграє рівно один (concurrent/retry →
                // 0 рядків → false). budget_reserved=1: одиницю бюджету зарезервовано через admission ДО
                // цього виклику (ordering-i) — прапорець фіксує володіння для crash-форфейт-обліку.
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT OR IGNORE INTO send_dedup
                        (identity_id, message_id, status, result_json, budget_reserved, created_at)
                    VALUES ($id, $msg, $status, NULL, 1, $created);
                    SELECT changes();
                    """;
                command.Parameters.AddWithValue("$id", identityId);
                command.Parameters.AddWithValue("$msg", messageId);
                command.Parameters.AddWithValue("$status", SendDedupRecord.StatusPending);
                command.Parameters.AddWithValue("$created", FormatTimestamp(now));
                return Convert.ToInt32(command.ExecuteScalar()) == 1;
            });
        }
    }

    /// <inheritdoc />
    public void CompleteSend(string identityId, string messageId, string resultJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(resultJson);

        lock (_sync)
        {
            WithRetry(() =>
            {
                // Умовний перехід pending→done: лише поки status='pending' (resume-гонка не перезапише done).
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE send_dedup
                    SET status = $done, result_json = $json
                    WHERE identity_id = $id AND message_id = $msg AND status = $pending;
                    """;
                command.Parameters.AddWithValue("$done", SendDedupRecord.StatusDone);
                command.Parameters.AddWithValue("$json", resultJson);
                command.Parameters.AddWithValue("$id", identityId);
                command.Parameters.AddWithValue("$msg", messageId);
                command.Parameters.AddWithValue("$pending", SendDedupRecord.StatusPending);
                command.ExecuteNonQuery();
                return 0;
            });
        }
    }

    /// <inheritdoc />
    public void Backup(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        lock (_sync)
        {
            var destinationConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString();

            using var destination = new SqliteConnection(destinationConnectionString);
            destination.Open();
            // Онлайн-backup (консистентний знімок, сумісний із WAL). Шифрування місця призначення —
            // платформне (окремий encrypted-том; R3.7/G4) — див. deploy/DEPLOYMENT.md.
            _connection.BackupDatabase(destination);
            _logger.LogInformation("Durable-стор забекаплено (online backup).");
        }
    }

    /// <summary>Closes the underlying connection.</summary>
    public void Dispose() => _connection.Dispose();

    private IReadOnlyList<string> ReadLinkedAccounts(string identityId)
    {
        var accounts = new List<string>();
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT account FROM identity_accounts WHERE identity_id = $id ORDER BY account;";
        command.Parameters.AddWithValue("$id", identityId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            accounts.Add(reader.GetString(0));
        return accounts;
    }

    // Спільне зв'язування параметрів INSERT для auth_tokens (insert + rotate-insert).
    private static void BindToken(SqliteCommand command, TokenRecord token)
    {
        command.Parameters.AddWithValue("$hash", token.TokenHash);
        command.Parameters.AddWithValue("$ver", token.PepperVersion);
        command.Parameters.AddWithValue("$identity", token.IdentityId);
        command.Parameters.AddWithValue("$expires", FormatTimestamp(token.ExpiresAt));
        command.Parameters.AddWithValue("$revoked", token.IsRevoked ? 1 : 0);
        command.Parameters.AddWithValue("$created", FormatTimestamp(token.CreatedAt));
    }

    private void RunIntegrityCheck()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = command.ExecuteScalar() as string;

        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            // G4: пошкоджена БД → fail-start із чітким логом. Відновлення — з backup (DEPLOYMENT.md).
            throw new InvalidOperationException(
                $"Durable БД не пройшла integrity_check (результат: '{result ?? "(null)"}') — " +
                "старт заблоковано (G4). Відновіть БД із шифрованого backup перед повторним запуском.");
        }
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    // Ретрай на SQLITE_BUSY/LOCKED (доповнює busy_timeout — belt-and-suspenders, proposal).
    private T WithRetry<T>(Func<T> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (SqliteException ex) when (IsBusy(ex) && attempt < MaxBusyRetries)
            {
                _logger.LogDebug("SQLITE_BUSY (спроба {Attempt}/{Max}) — ретрай.", attempt, MaxBusyRetries);
                Thread.Sleep(TimeSpan.FromMilliseconds(20 * attempt));
            }
        }
    }

    private static bool IsBusy(SqliteException ex) =>
        ex.SqliteErrorCode is 5 /* SQLITE_BUSY */ or 6 /* SQLITE_LOCKED */;

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

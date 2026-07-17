using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>
/// Versioned schema migration runner keyed on <c>PRAGMA user_version</c> (G4). Applies every
/// migration newer than the database's current version inside a transaction, in order.
/// </summary>
/// <remarks>
/// Нові зміни (authn/admission/group-claim) додають свої міграції у кінець <see cref="Migrations"/>
/// зі зростаючим номером версії — цей раннер лишається їхнім спільним механізмом.
/// </remarks>
internal static class SchemaMigrator
{
    /// <summary>The highest schema version this build knows about.</summary>
    public static int LatestVersion => Migrations[^1].Version;

    // Впорядкований список міграцій. Кожна виконується рівно один раз, коли user_version < Version.
    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1,
            """
            CREATE TABLE identities (
                id          TEXT PRIMARY KEY,
                role        TEXT NOT NULL,
                created_at  TEXT NOT NULL
            ) STRICT;

            CREATE TABLE identity_accounts (
                identity_id TEXT NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                account     TEXT NOT NULL,
                PRIMARY KEY (identity_id, account)
            ) STRICT;

            CREATE TABLE account_bindings (
                account            TEXT PRIMARY KEY,
                owning_identity_id TEXT NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                is_private         INTEGER NOT NULL,
                created_at         TEXT NOT NULL
            ) STRICT;

            CREATE INDEX ix_account_bindings_identity ON account_bindings(owning_identity_id);
            """),

        (2,
            """
            CREATE TABLE auth_tokens (
                token_hash     TEXT PRIMARY KEY,
                pepper_version INTEGER NOT NULL,
                identity_id    TEXT NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                expires_at     TEXT NOT NULL,
                is_revoked     INTEGER NOT NULL DEFAULT 0,
                created_at     TEXT NOT NULL
            ) STRICT;

            CREATE INDEX ix_auth_tokens_identity ON auth_tokens(identity_id);

            CREATE TABLE device_secrets (
                identity_id TEXT PRIMARY KEY REFERENCES identities(id) ON DELETE CASCADE,
                public_key  TEXT NOT NULL,
                algorithm   TEXT NOT NULL,
                is_revoked  INTEGER NOT NULL DEFAULT 0,
                created_at  TEXT NOT NULL
            ) STRICT;
            """),

        (3,
            """
            CREATE TABLE budget_reservations (
                window_key   TEXT NOT NULL,
                block_index  INTEGER NOT NULL,
                block_size   INTEGER NOT NULL,
                reserved_at  TEXT NOT NULL,
                PRIMARY KEY (window_key, block_index)
            ) STRICT;

            CREATE TABLE known_recipients (
                identity_id     TEXT NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                recipient_hash  TEXT NOT NULL,
                first_seen      TEXT NOT NULL,
                PRIMARY KEY (identity_id, recipient_hash)
            ) STRICT;
            """),

        // v4 (add-invites-admin): інвайт-гейт (one-time код, per-code attempt cap, атомарний consume —
        // W20/G12) + shared-bot abuse-лог (домен-розділений HMAC із per-record salt, W24).
        (4,
            """
            CREATE TABLE invites (
                code_hash        TEXT PRIMARY KEY,
                created_by       TEXT NOT NULL,
                consumed         INTEGER NOT NULL DEFAULT 0,
                attempt_count    INTEGER NOT NULL DEFAULT 0,
                max_attempts     INTEGER NOT NULL,
                expires_at       TEXT NOT NULL,
                consumed_by      TEXT,
                created_at       TEXT NOT NULL
            ) STRICT;

            CREATE TABLE abuse_log (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                event_type    TEXT NOT NULL,
                subject_hmac  TEXT NOT NULL,
                salt          TEXT NOT NULL,
                logged_at     TEXT NOT NULL
            ) STRICT;
            """),

        // v5 (add-invites-admin, секція 2): admin-креденшели (SPKI mTLS-cert → admin identity, task 2.2)
        // + tamper-evident audit-trail із hash-chain (task 3.2/M8, окремий від abuse-логу).
        (5,
            """
            CREATE TABLE admin_credentials (
                spki_sha256  TEXT PRIMARY KEY,
                identity_id  TEXT NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                revoked      INTEGER NOT NULL DEFAULT 0,
                created_at   TEXT NOT NULL
            ) STRICT;

            CREATE TABLE audit_log (
                seq            INTEGER PRIMARY KEY,
                event_type     TEXT NOT NULL,
                actor_identity TEXT NOT NULL,
                detail_hash    TEXT NOT NULL,
                prev_hash      TEXT NOT NULL,
                entry_hash     TEXT NOT NULL,
                logged_at      TEXT NOT NULL
            ) STRICT;
            """),

        // v6 (add-group-claim-receive, секція 2): group-claim гейт для (identity, group). One-time
        // claim-код (group-agnostic при видачі — T3) + (identity, group, anchorAci) binding (T2).
        // code_hash = at-rest SHA-256 (код сам ≥96-bit секрет, як інвайти); binding несе anchor_aci
        // (ACI вставника, фіксує сканер) + expires_at (backstop-TTL) + revoked (revoke-каскад/anchor-churn).
        (6,
            """
            CREATE TABLE group_claims (
                code_hash   TEXT PRIMARY KEY,
                created_by  TEXT NOT NULL,
                consumed    INTEGER NOT NULL DEFAULT 0,
                expires_at  TEXT NOT NULL,
                created_at  TEXT NOT NULL
            ) STRICT;

            CREATE TABLE group_bindings (
                binding_id   TEXT PRIMARY KEY,
                identity_id  TEXT NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                group_id     TEXT NOT NULL,
                anchor_aci   TEXT NOT NULL,
                expires_at   TEXT NOT NULL,
                revoked      INTEGER NOT NULL DEFAULT 0,
                created_at   TEXT NOT NULL
            ) STRICT;

            CREATE INDEX ix_group_bindings_identity ON group_bindings(identity_id);
            CREATE INDEX ix_group_bindings_group ON group_bindings(group_id);
            """),
    ];

    /// <summary>Migrates <paramref name="connection"/> up to <see cref="LatestVersion"/>.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="logger">Logger for migration progress.</param>
    /// <returns>The resulting schema version.</returns>
    public static int Migrate(SqliteConnection connection, ILogger logger)
    {
        var current = GetUserVersion(connection);

        foreach (var (version, sql) in Migrations)
        {
            if (current >= version)
                continue;

            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            SetUserVersion(connection, transaction, version);
            transaction.Commit();

            logger.LogInformation("Застосовано міграцію durable-схеми → user_version={Version}.", version);
            current = version;
        }

        return current;
    }

    /// <summary>Reads <c>PRAGMA user_version</c>.</summary>
    public static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // user_version не приймає параметра — version є довіреним int із нашого списку міграцій.
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }
}

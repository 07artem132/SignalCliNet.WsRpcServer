namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>
/// The durable, single-copy store for identity and account-binding state (G4/R3.5). Backed by an
/// embedded SQLite database on the encrypted data volume; opened once at startup after an integrity
/// check and schema migration.
/// </summary>
/// <remarks>
/// Фаза 1 ще не має споживачів цих таблиць у RPC-шляху — це фундамент (T1), який наповнюють пізніші
/// зміни (authn токени/device-секрети, admission бюджет, group-claim bindings). Тут — рушій БД,
/// migration-runner, integrity-check, backup та базові identity/binding-таблиці.
/// </remarks>
public interface IDurableStore
{
    /// <summary>The applied schema version (<c>PRAGMA user_version</c>).</summary>
    int SchemaVersion { get; }

    /// <summary>Inserts or updates an identity and replaces its linked-account set (atomic).</summary>
    void UpsertIdentity(IdentityRecord identity);

    /// <summary>Returns the identity with <paramref name="id"/>, or <c>null</c> if absent.</summary>
    IdentityRecord? GetIdentity(string id);

    /// <summary>Returns all identities.</summary>
    IReadOnlyList<IdentityRecord> GetIdentities();

    /// <summary>Inserts or updates the binding for its account (keyed by account).</summary>
    void UpsertAccountBinding(AccountBinding binding);

    /// <summary>Returns the binding for <paramref name="account"/>, or <c>null</c> if absent.</summary>
    AccountBinding? GetAccountBinding(string account);

    /// <summary>Returns all bindings owned by <paramref name="identityId"/>.</summary>
    IReadOnlyList<AccountBinding> GetBindingsForIdentity(string identityId);

    /// <summary>Inserts a freshly issued access token (its at-rest HMAC is the primary key).</summary>
    void InsertToken(TokenRecord token);

    /// <summary>Returns the token with <paramref name="tokenHash"/>, or <c>null</c> if absent.</summary>
    TokenRecord? GetToken(string tokenHash);

    /// <summary>
    /// Rotates a token atomically (W21 zero-overlap): revokes <paramref name="oldTokenHash"/> and
    /// inserts <paramref name="newToken"/> in a single transaction.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="oldTokenHash"/> does not exist — a rotation without a predecessor is a logic error.
    /// </exception>
    void RotateToken(string oldTokenHash, TokenRecord newToken);

    /// <summary>
    /// Revokes all of an identity's credentials atomically (task 1.4 cascade): every access token and
    /// the device secret of <paramref name="identityId"/> are marked revoked in a single transaction.
    /// </summary>
    void RevokeIdentityCredentials(string identityId);

    /// <summary>Inserts or updates the device secret for its identity (keyed by identity).</summary>
    void UpsertDeviceSecret(DeviceSecretRecord secret);

    /// <summary>Returns the device secret for <paramref name="identityId"/>, or <c>null</c> if absent.</summary>
    DeviceSecretRecord? GetDeviceSecret(string identityId);

    /// <summary>
    /// Counts the durably-reserved budget blocks for <paramref name="windowKey"/> (admission W13). The
    /// block count times the block size is the units already reserved this window.
    /// </summary>
    int CountBudgetBlocks(string windowKey);

    /// <summary>
    /// Durably reserves one budget block (<paramref name="blockIndex"/> units of
    /// <paramref name="blockSize"/>) for <paramref name="windowKey"/> (admission W13).
    /// </summary>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">
    /// The <c>(window_key, block_index)</c> pair already exists — a double-reserve, which MUST surface as
    /// an error (never silently succeed).
    /// </exception>
    void InsertBudgetBlock(string windowKey, int blockIndex, int blockSize, DateTimeOffset reservedAt);

    /// <summary>
    /// Returns whether <paramref name="recipientHash"/> is already a known (previously-contacted) recipient
    /// of <paramref name="identityId"/> (admission D2). The stored value is a hash — never a raw identifier.
    /// </summary>
    bool IsKnownRecipient(string identityId, string recipientHash);

    /// <summary>
    /// Records <paramref name="recipientHash"/> as a known recipient of <paramref name="identityId"/>
    /// (admission D2), preserving the original <paramref name="firstSeen"/> on repeat contact (idempotent).
    /// </summary>
    void UpsertKnownRecipient(string identityId, string recipientHash, DateTimeOffset firstSeen);

    /// <summary>
    /// Inserts a freshly issued one-time invite (its at-rest code hash is the primary key; add-invites-admin).
    /// </summary>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">
    /// The <c>code_hash</c> already exists — a duplicate invite, which MUST surface as an error.
    /// </exception>
    void InsertInvite(InviteRecord invite);

    /// <summary>Returns the invite with <paramref name="codeHash"/>, or <c>null</c> if absent.</summary>
    InviteRecord? GetInvite(string codeHash);

    /// <summary>
    /// Atomically records a redeem attempt against <paramref name="codeHash"/> and, if the invite is still
    /// redeemable, consumes it for <paramref name="consumedByIdentityId"/> — all in ONE transaction (W20/G12).
    /// </summary>
    /// <remarks>
    /// У одній транзакції: (1) <c>attempt_count += 1</c> (кожна спроба, зокрема невдала, рахується — G12);
    /// (2) перечитуємо; якщо інвайт відсутній / прострочений / уже consumed / attempt_count &gt; max_attempts
    /// → повертаємо <c>false</c> (attempt уже інкрементнутий); (3) інакше conditional
    /// <c>UPDATE … SET consumed=1 WHERE consumed=0</c> — rows-affected==1 виграє (true), програна гонка → false.
    /// Уся логіка під транзакцією: паралельні guesses не обходять cap.
    /// </remarks>
    /// <param name="codeHash">At-rest hash of the presented code.</param>
    /// <param name="consumedByIdentityId">The identity that redeems the invite on success.</param>
    /// <param name="now">Current time (for the expiry check).</param>
    /// <returns><c>true</c> if this call consumed the invite; otherwise <c>false</c>.</returns>
    bool TryConsumeInvite(string codeHash, string consumedByIdentityId, DateTimeOffset now);

    /// <summary>Appends an abuse-log record (task 3.3) and returns its rowid.</summary>
    int AppendAbuseLog(AbuseLogRecord record);

    /// <summary>
    /// Writes a consistent online backup of the database to <paramref name="destinationPath"/>
    /// (G4 — the destination SHOULD be a separate encrypted volume; see deploy/DEPLOYMENT.md).
    /// </summary>
    void Backup(string destinationPath);
}

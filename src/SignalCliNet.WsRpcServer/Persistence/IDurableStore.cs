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
    /// Writes a consistent online backup of the database to <paramref name="destinationPath"/>
    /// (G4 — the destination SHOULD be a separate encrypted volume; see deploy/DEPLOYMENT.md).
    /// </summary>
    void Backup(string destinationPath);
}

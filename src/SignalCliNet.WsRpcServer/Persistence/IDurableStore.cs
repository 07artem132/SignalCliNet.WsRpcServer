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

    /// <summary>
    /// Writes a consistent online backup of the database to <paramref name="destinationPath"/>
    /// (G4 — the destination SHOULD be a separate encrypted volume; see deploy/DEPLOYMENT.md).
    /// </summary>
    void Backup(string destinationPath);
}

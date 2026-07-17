namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>
/// A durable <c>(identity, group)</c> authorization binding created by confirming a group-claim code
/// (add-group-claim-receive, task 2.2). This is the recipient-level grant that gates group sends through
/// the shared bot (T2 anchor-model).
/// </summary>
/// <remarks>
/// T2 (anchor-модель): binding несе <see cref="AnchorAci"/> — ACI вставника коду, зафіксований сканером при
/// confirm. Перед КОЖНИМ send через бот перевіряється <c>anchorAci ∈ members(group)</c>: anchor більше не
/// член → send-відмова + <see cref="Revoked"/>=1. <see cref="ExpiresAt"/> — backstop-TTL (форсує re-claim).
/// <see cref="IdentityId"/> має FK на <c>identities(id)</c> з <c>ON DELETE CASCADE</c>, а revoke identity
/// каскадно виставляє <see cref="Revoked"/>=1 (task 2.4).
/// </remarks>
/// <param name="BindingId">Унікальний id binding-у (primary key; CSPRNG-opaque).</param>
/// <param name="IdentityId">Identity, якій належить binding (U — з <c>claim.created_by</c>).</param>
/// <param name="GroupId">Група, яку зафіксував сканер за місцем появи коду.</param>
/// <param name="AnchorAci">ACI вставника коду (anchor); перевіряється на членство при кожному send.</param>
/// <param name="ExpiresAt">Абсолютний backstop-термін дії binding-у (created + TTL).</param>
/// <param name="Revoked">Чи відкликано binding (send через нього відмовляє).</param>
/// <param name="CreatedAt">Час створення (UTC).</param>
public sealed record GroupBindingRecord(
    string BindingId,
    string IdentityId,
    string GroupId,
    string AnchorAci,
    DateTimeOffset ExpiresAt,
    bool Revoked,
    DateTimeOffset CreatedAt);

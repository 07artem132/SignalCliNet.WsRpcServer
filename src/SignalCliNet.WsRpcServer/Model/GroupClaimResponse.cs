namespace SignalCliNet.WsRpcServer.Model;

/// <summary>
/// RPC result of <c>requestGroupClaim</c>: the freshly-minted plaintext claim <see cref="Code"/> (returned
/// to the requesting identity exactly once, to paste into the target group chat) and its absolute
/// <see cref="ExpiresAt"/>.
/// </summary>
/// <remarks>
/// A1 (secret-DTO): цей тип несе ПЛЕЙНТЕКСТ-КОД (секрет), тож він СВІДОМО НЕ реєструється у
/// <c>SignalCliSerializerContext</c> (source-gen) — інакше guard-тест <c>SerializerContextSecretGuardTests</c>
/// впав би. Форматтер сесії серіалізує його reflection-fallback'ом (як <c>CreateInviteResponse</c> /
/// <c>RedeemInviteResponse</c>). Значення <see cref="Code"/> — секрет: сервер його НІКОЛИ не логує.
/// </remarks>
/// <param name="Code">The plaintext group-claim code (high-entropy secret — never logged; delivered once).</param>
/// <param name="ExpiresAt">Absolute expiry of the claim (created + TTL).</param>
public sealed record GroupClaimResponse(string Code, DateTimeOffset ExpiresAt);

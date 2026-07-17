namespace SignalCliNet.WsRpcServer.Model;

/// <summary>
/// RPC result of <c>createInvite</c>: the freshly-minted plaintext invite <see cref="Code"/> (returned to
/// the issuing admin exactly once, for out-of-band delivery) and its absolute <see cref="ExpiresAt"/>.
/// </summary>
/// <remarks>
/// A1 (secret-DTO): цей тип несе ПЛЕЙНТЕКСТ-КОД (секрет для передачі новому користувачу), тож він
/// СВІДОМО НЕ реєструється у <c>SignalCliSerializerContext</c> (source-gen) — інакше guard-тест
/// <c>SerializerContextSecretGuardTests</c> впав би на властивості <see cref="Code"/>. Форматтер сесії
/// серіалізує його reflection-fallback'ом (<c>DefaultJsonTypeInfoResolver</c>), як <c>RedeemInviteResponse</c>.
/// Значення <see cref="Code"/> — секрет: сервер його НІКОЛИ не логує.
/// </remarks>
/// <param name="Code">The plaintext invite code (high-entropy secret — never logged; delivered once).</param>
/// <param name="ExpiresAt">Absolute expiry of the invite (created + TTL).</param>
public sealed record CreateInviteResponse(string Code, DateTimeOffset ExpiresAt);

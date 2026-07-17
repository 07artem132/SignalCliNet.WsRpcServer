namespace SignalCliNet.WsRpcServer.Model;

/// <summary>
/// RPC result of <c>redeemInvite</c>: the freshly-provisioned <see cref="IdentityId"/> and its first
/// access <see cref="Token"/> (plaintext, returned to the client exactly once).
/// </summary>
/// <remarks>
/// A1 (secret-DTO): цей тип несе ПЛЕЙНТЕКСТ-ТОКЕН, тож він СВІДОМО НЕ реєструється у
/// <c>SignalCliSerializerContext</c> (source-gen) — інакше guard-тест <c>SerializerContextSecretGuard</c>
/// впав би на властивості <see cref="Token"/>. Форматтер сесії серіалізує його reflection-fallback'ом
/// (<c>DefaultJsonTypeInfoResolver</c>, скомбінований із source-gen контекстом), як і решту незареєстрованих
/// payload-типів. Це той самий принцип, що й renewal-token в ack <c>pop.prove</c>: плейнтекст-токени не
/// проходять через source-gen серіалізатор. Значення <see cref="Token"/> — секрет: сервер його НІКОЛИ не
/// логує (без зайвих полів у DTO).
/// </remarks>
/// <param name="IdentityId">The new identity id provisioned by the redemption (opaque; never a number).</param>
/// <param name="Token">The identity's first access token (plaintext — sensitive, never logged).</param>
public sealed record RedeemInviteResponse(string IdentityId, string Token);

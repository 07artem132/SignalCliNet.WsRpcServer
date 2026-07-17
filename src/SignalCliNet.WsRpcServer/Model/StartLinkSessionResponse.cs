namespace SignalCliNet.WsRpcServer.Model;

/// <summary>
/// RPC result of <c>startLink</c>: the opaque one-time <see cref="SessionId"/> the client passes back to
/// <c>finishLink</c>, plus the <see cref="DeviceLinkUri"/> to render as a QR for a Signal primary device.
/// </summary>
/// <remarks>
/// A1: <see cref="DeviceLinkUri"/> НАВМИСНО серіалізується — це відповідь саме клієнту (він малює QR),
/// тож <c>[JsonIgnore]</c> НЕ потрібен. Але значення чутливе: сервер НІКОЛИ не логує URI (task 6 redact).
/// Імена властивостей не містять секрет-підстрок (token/secret/…), тож тип проходить
/// <c>SerializerContextSecretGuard</c>. Зареєстрований у <c>SignalCliSerializerContext</c> (source-gen).
/// </remarks>
/// <param name="SessionId">One-time 256-bit session id for <c>finishLink</c>.</param>
/// <param name="DeviceLinkUri">Signal device-link URI to render as a QR (sensitive — never logged).</param>
public sealed record StartLinkSessionResponse(string SessionId, string DeviceLinkUri);

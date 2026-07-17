using System.Text.Json.Serialization;
using SignalCli.Models.Signal.Accounts;
using SignalCli.Models.Signal.Devices;
using SignalCli.Models.Signal.Events;
using SignalCli.Models.Signal.Message;
using SignalCliNet.WsRpcServer.Model;
using SignalCliNet.WsRpcServer.Security.Admission;
using WsRpcServer;
using WsRpcServer.Events;


namespace SignalCliNet.WsRpcServer.Serialization;

/// <summary>
/// Source-generated JSON serialization context for better performance and AOT compatibility
/// </summary>
/// <remarks>
/// A1 (secret-DTO): будь-який тип, доданий сюди [JsonSerializable], серіалізується source-gen-ом. Тому
/// ЖОДНА серіалізовна властивість не сміє нести секрет (token/secret/signature/pepper/privatekey) без
/// явного <c>[JsonIgnore]</c> — інакше плейнтекст-секрет витече на дріт. Це пінить guard-тест
/// <c>SerializerContextSecretGuardTests</c> (рефлексія по зареєстрованих тут типах). Плейнтекст-токени
/// (authenticate/pop.prove ack) свідомо НЕ проходять через цей серіалізатор — їх пишуть вручну
/// Utf8JsonWriter-ом у сесії.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(RpcNotification))]
[JsonSerializable(typeof(HandshakeResponse))]
[JsonSerializable(typeof(ApiVersionRequirement))]
[JsonSerializable(typeof(RateLimitErrorData))]
[JsonSerializable(typeof(ListAccountsResponse))]
[JsonSerializable(typeof(SyncAccountsResponse))]
[JsonSerializable(typeof(StartLinkResponse))]
[JsonSerializable(typeof(StartLinkSessionResponse))]
[JsonSerializable(typeof(FinishLinkResponse))]
[JsonSerializable(typeof(SendMessageResponse))]
[JsonSerializable(typeof(TextMessageEventArgs))]
[JsonSerializable(typeof(ReactionEventArgs))]
[JsonSerializable(typeof(AttachmentEventArgs))]
[JsonSerializable(typeof(StickerEventArgs))]
[JsonSerializable(typeof(TypingEventArgs))]
[JsonSerializable(typeof(ReceiptEventArgs))]
[JsonSerializable(typeof(SyncEventArgs))]
[JsonSerializable(typeof(SignalEventTypes))]
[JsonSerializable(typeof(Dictionary<int, string>))]
public partial class SignalCliSerializerContext : JsonSerializerContext
{
}
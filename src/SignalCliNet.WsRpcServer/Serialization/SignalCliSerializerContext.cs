using System.Text.Json.Serialization;
using SignalCli.Models.Signal.Accounts;
using SignalCli.Models.Signal.Devices;
using SignalCli.Models.Signal.Events;
using SignalCliNet.WsRpcServer.Model;
using WsRpcServer;
using WsRpcServer.Events;


namespace SignalCliNet.WsRpcServer.Serialization;

/// <summary>
/// Source-generated JSON serialization context for better performance and AOT compatibility
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(RpcNotification))]
[JsonSerializable(typeof(ListAccountsResponse))]
[JsonSerializable(typeof(SyncAccountsResponse))]
[JsonSerializable(typeof(StartLinkResponse))]
[JsonSerializable(typeof(FinishLinkResponse))]
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
namespace SignalCliNet.WsRpcServer.Model;

[Flags]
public enum SignalEventTypes
{
    None = 0,
    TextMessages = 1 << 0,
    Reactions = 1 << 1,
    Attachments = 1 << 2,
    Stickers = 1 << 3,
    TypingNotifications = 1 << 4,
    Receipts = 1 << 5,
    Syncs = 1 << 6,
    All = TextMessages | Reactions | Attachments | Stickers | TypingNotifications | Receipts | Syncs
}
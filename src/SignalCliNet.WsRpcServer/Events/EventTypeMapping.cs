using SignalCli.Models.Signal.Events;
using SignalCliNet.WsRpcServer.Model;

namespace SignalCliNet.WsRpcServer.Events;

public static class EventTypeMapping
{
    private static readonly Dictionary<Type, EventTypeInfo> TypeMap = new()
    {
        { typeof(TextMessageEventArgs), new EventTypeInfo(SignalEventTypes.TextMessages, "signal.textMessage") },
        { typeof(ReactionEventArgs), new EventTypeInfo(SignalEventTypes.Reactions, "signal.reaction") },
        { typeof(AttachmentEventArgs), new EventTypeInfo(SignalEventTypes.Attachments, "signal.attachment") },
        { typeof(StickerEventArgs), new EventTypeInfo(SignalEventTypes.Stickers, "signal.sticker") },
        { typeof(TypingEventArgs), new EventTypeInfo(SignalEventTypes.TypingNotifications, "signal.typing") },
        { typeof(ReceiptEventArgs), new EventTypeInfo(SignalEventTypes.Receipts, "signal.receipt") },
        { typeof(SyncEventArgs), new EventTypeInfo(SignalEventTypes.Syncs, "signal.sync") }
    };

    private static readonly Dictionary<Type, EventTypeInfo> FastLookup = new();

    static EventTypeMapping()
    {
        foreach (var (type, info) in TypeMap)
        {
            FastLookup[type] = info;
        }
    }

    public static EventTypeInfo GetEventInfo<T>() where T : BaseSignalEventArgs
    {
        if (FastLookup.TryGetValue(typeof(T), out var info))
            return info;

        return new EventTypeInfo(SignalEventTypes.None, string.Empty);
    }
}
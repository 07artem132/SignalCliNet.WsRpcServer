using SignalCli.Models.Signal;
using SignalCli.Models.Signal.Events;
using SignalCliNet.WsRpcServer.Events;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Tests.TestSupport;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Events;

/// <summary>
/// Пінить GroupClaimScanner (add-group-claim-receive, tasks 1.2/1.3/3.1): матч коду у групі → Confirm
/// (група + anchor ACI); не-групове / без-anchor повідомлення НЕ обробляється; scan-and-drop — тіло/номери
/// НІКОЛИ не логуються.
/// </summary>
public sealed class GroupClaimScannerTests
{
    // 26-символьний Crockford-код (без I/L/O/U) — саме та форма, яку розпізнає LooksLikeCode.
    private const string ValidCode = "ABCDEFGHJKMNPQRSTVWXYZ0123";

    private const string SecretBody = "super-secret-message-body-xyz";
    private const string Phone = "+15557654321";

    private static TextMessageEventArgs Message(string account, string? text, string? groupId, string? anchorAci)
    {
        var dataMessage = new JsonDataMessage(
            0UL, text, null, null, null, null, null, null, null, null, null, null, null, null,
            groupId is null ? null : new JsonGroupInfo(groupId, null, 0, null), null);
        return new TextMessageEventArgs(
            SubscriptionId: 1, Account: account, DataMessage: dataMessage, Source: null,
            SourceNumber: Phone, SourceUuid: anchorAci, SourceName: null, SourceDevice: null,
            Timestamp: 0, ServerReceivedTimestamp: 0, ServerDeliveredTimestamp: 0);
    }

    [Fact]
    public async Task MatchesCode_InGroup_CallsConfirmWithGroupAndAnchor()
    {
        var confirmer = new RecordingConfirmer();
        using var scanner = new GroupClaimScanner(confirmer, new CapturingLogger<GroupClaimScanner>());

        // FIFO: спершу не-код повідомлення (не має дати Confirm), потім код — по його Confirm синхронізуємось.
        scanner.Enqueue(Message("+15550009999", "just chatting no code here", "grp-1", "aci-anchor"));
        scanner.Enqueue(Message("+15550009999", $"please use {ValidCode} thanks", "grp-1", "aci-anchor"));

        await confirmer.Signal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var (hash, group, anchor) = Assert.Single(confirmer.Calls);   // лише код-повідомлення дало Confirm
        Assert.Equal(GroupClaimService.ComputeCodeHash(ValidCode), hash);
        Assert.Equal("grp-1", group);
        Assert.Equal("aci-anchor", anchor);
    }

    [Fact]
    public async Task NonGroupMessage_NotProcessed()
    {
        var confirmer = new RecordingConfirmer();
        using var scanner = new GroupClaimScanner(confirmer, new CapturingLogger<GroupClaimScanner>());

        // DM (groupId=null) із кодом — сканер НЕ обробляє (claim фіксується лише у групі); потім груповий код.
        scanner.Enqueue(Message("+15550009999", $"code {ValidCode} in DM", groupId: null, anchorAci: "aci"));
        scanner.Enqueue(Message("+15550009999", $"code {ValidCode} in group", "grp-1", "aci"));

        await confirmer.Signal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(confirmer.Calls);            // лише груповий код
        Assert.Equal("grp-1", confirmer.Calls[0].GroupId);
    }

    [Fact]
    public async Task ScanAndDrop_NeverLogsBodyOrNumber()
    {
        var confirmer = new RecordingConfirmer();
        var log = new CapturingLogger<GroupClaimScanner>();
        using var scanner = new GroupClaimScanner(confirmer, log);

        scanner.Enqueue(Message("+15550009999", $"{SecretBody} {ValidCode}", "grp-1", "aci-anchor"));

        await confirmer.Signal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(log.AnyContains(SecretBody), "тіло повідомлення потрапило в лог (privacy 3.1)");
        Assert.False(log.AnyContains(Phone), "номер відправника потрапив у лог (privacy 3.1)");
    }

    // Фейковий confirmer: записує аргументи кожного Confirm і сигналить TCS на першому виклику.
    private sealed class RecordingConfirmer : IGroupClaimConfirmer
    {
        private readonly List<(string CodeHash, string GroupId, string AnchorAci)> _calls = [];
        private readonly object _sync = new();

        public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<(string CodeHash, string GroupId, string AnchorAci)> Calls
        {
            get { lock (_sync) { return _calls.ToList(); } }
        }

        public bool Confirm(string codeHash, string groupId, string anchorAci)
        {
            lock (_sync)
                _calls.Add((codeHash, groupId, anchorAci));
            Signal.TrySetResult();
            return true;
        }
    }
}

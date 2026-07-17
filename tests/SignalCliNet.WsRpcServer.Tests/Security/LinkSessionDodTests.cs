using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Devices;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Extensions;
using SignalCliNet.WsRpcServer.Interfaces;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Services;
using WsRpcServer.Core;
using WsRpcServer.Events;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// DoD-тести change add-link-sessions (tasks 7–9) через РЕАЛЬНИЙ шлях: живий сервер, справжні
/// <see cref="ClientWebSocket"/>-конекти, повний auth (токен+PoP), реальні chokepoint/адаптер/
/// LinkSessionStore; замоканий лише upstream <see cref="ISignalDevices"/>.
/// 7 — finishLink чужою identity → помилка, сесія ЖИВА (анти-griefing);
/// 8 — повторний finishLink тим самим sessionId → відмова (one-time);
/// 9 — не-admin лінкує до існуючого бот-акаунта → відмова, upstream не викликано.
/// (10 — per-call timeout ≥130с — відкладено до upstream-зміни SignalCli.NET, див. tasks.md task 4.)
/// </summary>
public sealed class LinkSessionDodTests : IAsyncLifetime
{
    private const string RealProto = "jsonrpc.dod";
    private const string BotAccount = "+15550009999";
    private const string LinkUri = "sgnl://linkdevice?uuid=dod-test-uri";

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-link-dod-" + Guid.NewGuid().ToString("N"));

    private readonly Mock<ISignalDevices> _devicesMock = new();

    private ServiceProvider _services = null!;
    private SignalRpcServer _server = null!;
    private DurableStore _store = null!;
    private DeviceEnrollmentService _enrollment = null!;
    private int _port;
    private int _finishLinkCalls;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_dataDir, "secrets"));
        File.WriteAllText(
            Path.Combine(_dataDir, "secrets", "pepper_token"),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        File.WriteAllText(
            Path.Combine(_dataDir, "secrets", "pepper_abuse"),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        _devicesMock
            .Setup(d => d.StartLinkAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StartLinkResponse(LinkUri));
        _devicesMock
            .Setup(d => d.FinishLinkAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref _finishLinkCalls);
                return Task.FromResult(new FinishLinkResponse("+15551234567"));
            });

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Server:Auth:Enabled"] = "true",
                ["Server:Auth:AuthTimeoutSeconds"] = "15",
                ["Server:Auth:MaxUnauthenticatedPerIp"] = "64",
            }).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.Configure<PersistenceOptions>(o => o.DataDirectory = _dataDir);

        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
        services.AddSingleton<IDurableStore>(_store);

        services.AddAuthnCore(configuration);
        services.AddAdmissionCore(configuration);
        services.AddSingleton(new JsonRpcServerConfig());

        // Реальні реєстр/політики/devices-адаптер; upstream signal-cli — мок. Решта поверхні — пусті моки.
        services.AddSingleton<global::WsRpcServer.Services.IRpcServiceRegistry, RpcServiceRegistry>();
        services.AddSingleton<IRpcPolicyRegistry, SignalRpcPolicyRegistry>();
        services.AddSingleton<LinkSessionStore>();
        services.AddSingleton(_devicesMock.Object);
        services.AddScoped<ISignalDevicesRpc, SignalDevicesRpcAdapter>();
        services.AddSingleton(Mock.Of<ISignalAccountsRpc>());
        services.AddSingleton(Mock.Of<ISignalGroupsRpc>());
        services.AddSingleton(Mock.Of<ISignalMessageRpc>());
        services.AddSingleton(Mock.Of<ISystemRpc>());
        services.AddSingleton(Mock.Of<IEventProcessor>());

        _services = services.BuildServiceProvider();
        _enrollment = _services.GetRequiredService<DeviceEnrollmentService>();

        _port = GetFreePort();
        _server = new SignalRpcServer(
            IPAddress.Loopback, _port, _services,
            _services.GetRequiredService<ILogger<SignalRpcServer>>());
        Assert.True(_server.Start());
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server.Stop();
        _server.Dispose();
        _services.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort прибирання темп-каталогу
        }

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- DoD 7 (анти-griefing)

    [Fact]
    public async Task ForeignIdentity_FinishLink_Fails_AndSessionSurvives()
    {
        var keyA = Enroll("link-a", out var tokenA);
        var keyB = Enroll("link-b", out var tokenB);

        // A стартує link-сесію.
        using var wsA = await ConnectAsync([tokenA, RealProto]);
        await CompletePopAsync(wsA, keyA);
        var sessionId = await StartLinkAsync(wsA);

        // B (чужа identity) намагається загасити сесію A → помилка; сесія A НЕ знищена.
        using var wsB = await ConnectAsync([tokenB, RealProto]);
        await CompletePopAsync(wsB, keyB);
        using var foreign = await CallAsync(wsB, FinishLinkFrame(2, sessionId));
        Assert.True(foreign.RootElement.TryGetProperty("error", out var foreignError));
        Assert.Equal(0, Volatile.Read(ref _finishLinkCalls)); // upstream не чіпався

        // A завершує СВОЮ сесію успішно — доказ, що griefing її не вбив.
        using var own = await CallAsync(wsA, FinishLinkFrame(3, sessionId));
        Assert.False(own.RootElement.TryGetProperty("error", out _),
            "сесію A вбито чужим finishLink (griefing)");
        Assert.Equal(1, Volatile.Read(ref _finishLinkCalls));
    }

    // ---------------------------------------------------------------- DoD 8 (one-time)

    [Fact]
    public async Task RepeatedFinishLink_SameSession_IsRefused()
    {
        var key = Enroll("link-once", out var token);

        using var ws = await ConnectAsync([token, RealProto]);
        await CompletePopAsync(ws, key);
        var sessionId = await StartLinkAsync(ws);

        using var first = await CallAsync(ws, FinishLinkFrame(2, sessionId));
        Assert.False(first.RootElement.TryGetProperty("error", out _));

        // Повторне гашення того самого SessionID → відмова (one-time), upstream вдруге не викликано.
        using var second = await CallAsync(ws, FinishLinkFrame(3, sessionId));
        Assert.True(second.RootElement.TryGetProperty("error", out var error));
        Assert.Equal(-32004, error.GetProperty("code").GetInt32());
        Assert.Equal(1, Volatile.Read(ref _finishLinkCalls));
    }

    // ---------------------------------------------------------------- DoD 9 (shared-bot → admin-only)

    [Fact]
    public async Task NonAdmin_LinkingToExistingBotAccount_IsRefused()
    {
        // Бот-акаунт вже прив'язаний до іншої identity у сторі.
        _store.UpsertIdentity(new IdentityRecord("bot-owner", "user", [BotAccount], DateTimeOffset.UtcNow));
        _store.UpsertAccountBinding(new AccountBinding(BotAccount, "bot-owner", IsPrivate: false, DateTimeOffset.UtcNow));

        var key = Enroll("link-nonadmin", out var token);

        using var ws = await ConnectAsync([token, RealProto]);
        await CompletePopAsync(ws, key);

        // Не-admin цілиться target-ом у СПІЛЬНИЙ (вже відомий) бот-акаунт → відмова ДО upstream.
        using var denied = await CallAsync(ws,
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"startLink\",\"params\":{\"targetAccount\":\"" +
            BotAccount + "\"}}");
        Assert.True(denied.RootElement.TryGetProperty("error", out var error));
        Assert.Equal(-32001, error.GetProperty("code").GetInt32());
        _devicesMock.Verify(d => d.StartLinkAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ================================================================ хелпери

    private ECDsa Enroll(string id, out string token)
    {
        _store.UpsertIdentity(new IdentityRecord(id, "user", [], DateTimeOffset.UtcNow));
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        (token, _) = _enrollment.Enroll(id, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return key;
    }

    private async Task<string> StartLinkAsync(ClientWebSocket ws)
    {
        using var doc = await CallAsync(ws, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"startLink\",\"params\":{}}");
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
        return doc.RootElement.GetProperty("result").GetProperty("sessionId").GetString()!;
    }

    private static string FinishLinkFrame(int id, string sessionId) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"finishLink\",\"params\":{\"sessionId\":\"" +
        sessionId + "\",\"deviceName\":\"dod\"}}";

    private async Task<ClientWebSocket> ConnectAsync(string[] subprotocols)
    {
        var ws = new ClientWebSocket();
        foreach (var proto in subprotocols)
            ws.Options.AddSubProtocol(proto);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/"), Cts().Token);
        return ws;
    }

    private static CancellationTokenSource Cts() => new(TimeSpan.FromSeconds(10));

    private static async Task SendJsonAsync(ClientWebSocket ws, string json) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, Cts().Token);

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        var result = await ws.ReceiveAsync(buffer, Cts(). Token);
        Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
        return JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, 0, result.Count).ToArray());
    }

    private static async Task<JsonDocument> CallAsync(ClientWebSocket ws, string frame)
    {
        await SendJsonAsync(ws, frame);
        return await ReceiveJsonAsync(ws);
    }

    private static async Task CompletePopAsync(ClientWebSocket ws, ECDsa key)
    {
        using var challenge = await ReceiveJsonAsync(ws);
        Assert.Equal("pop.challenge", challenge.RootElement.GetProperty("method").GetString());
        var p = challenge.RootElement.GetProperty("params");
        var nonce = p.GetProperty("nonce").GetString()!;
        var connId = p.GetProperty("connId").GetString()!;
        var signature = Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes(nonce + connId), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        using var ack = await CallAsync(ws,
            "{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"pop.prove\",\"params\":{\"signature\":\"" +
            signature + "\"}}");
        Assert.Equal("ok", ack.RootElement.GetProperty("result").GetProperty("status").GetString());
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

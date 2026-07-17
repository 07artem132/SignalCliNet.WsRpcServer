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
using SignalCli.Models.Signal.Message;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Extensions;
using SignalCliNet.WsRpcServer.Interfaces;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Services;
using WsRpcServer.Core;
using WsRpcServer.Events;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security.Admission;

/// <summary>
/// DoD-тести секції 5 (add-admission-rate-limits) через РЕАЛЬНИЙ шлях: живий сервер + справжні
/// <see cref="ClientWebSocket"/>-конекти, повний auth (токен+PoP), РЕАЛЬНИЙ chokepoint із admission;
/// замоканий лише бекенд <see cref="ISignalMessageRpc"/> (лічильник викликів = «скільки send-ів
/// реально пройшло»). 5.1 G3 бюджет=1 → рівно 1; 5.2 G2 floor; 5.3 W13 survive-restart/форфейт;
/// 5.4 G6 pending не росте; 5.5 64KB + auth-timeout; 5.6 S9 in-band -32005 retry_after / 4429.
/// </summary>
public sealed class AdmissionDodTests : IDisposable
{
    private const string RealProto = "jsonrpc.dod";
    private const string Account = "+15551230001";
    private const string Recipient = "+15551230002";

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-adm-dod-" + Guid.NewGuid().ToString("N"));

    private readonly List<ServerStack> _stacks = [];

    // ---------------------------------------------------------------- 5.1 (G3)

    [Fact]
    public async Task ParallelSends_UnderBudgetOfOne_ExactlyOnePasses()
    {
        using var stack = BuildStack(new()
        {
            ["Server:Admission:AggregateBudgetPerWindow"] = "1",
            ["Server:Admission:ReserveBlockSize"] = "1",
            ["Server:Admission:PerUserFloorPerWindow"] = "1",
        });

        // 4 РІЗНІ identity × 1 конект (повний auth+PoP), кожна шле 1 send ОДНОЧАСНО: floor=1 пускає
        // всіх повз quota, new-recipient повз (унікальні), тож саме БЮДЖЕТ=1 вирішує під конкуренцією —
        // рівно один send досягає бекенда, решта -32005 (G3-атомарність через реальний шлях).
        var clients = new List<ClientWebSocket>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var key = stack.EnrollIdentity($"dod-g3-{i}", out var token);
                var ws = await ConnectAsync(stack.Port, [token, RealProto]);
                await CompletePopAsync(ws, key);
                clients.Add(ws);
            }

            var sends = clients.Select((ws, i) => SendAndReceiveResponseAsync(ws, SendFrame(i + 1))).ToArray();
            var responses = await Task.WhenAll(sends);

            var ok = responses.Count(r => !r.RootElement.TryGetProperty("error", out _));
            var limited = responses.Count(r =>
                r.RootElement.TryGetProperty("error", out var e) && e.GetProperty("code").GetInt32() == -32005);

            Assert.Equal(1, ok);
            Assert.Equal(3, limited);
            Assert.Equal(1, stack.BackendCalls);
        }
        finally
        {
            foreach (var ws in clients)
                ws.Dispose();
        }
    }

    // ---------------------------------------------------------------- 5.2 (G2)

    [Fact]
    public async Task HeavyUser_CannotStarveOtherUsersFloor()
    {
        using var stack = BuildStack(new()
        {
            ["Server:Admission:AggregateBudgetPerWindow"] = "4",
            ["Server:Admission:ReserveBlockSize"] = "1",
            ["Server:Admission:PerUserFloorPerWindow"] = "1",
        });
        var keyA = stack.EnrollIdentity("dod-heavy", out var tokenA, account: Account);
        var keyB = stack.EnrollIdentity("dod-light", out var tokenB, account: "+15551230003");

        // Юзер А молотить send-и до відмови (вижирає fair-share понад свій floor)...
        using (var wsA = await ConnectAsync(stack.Port, [tokenA, RealProto]))
        {
            await CompletePopAsync(wsA, keyA);
            for (var i = 0; i < 10; i++)
            {
                using var r = await SendAndReceiveResponseAsync(wsA, SendFrame(i + 1));
                if (r.RootElement.TryGetProperty("error", out _))
                    break;
            }
        }

        // ...але floor юзера Б гарантований — його перший send проходить (G2, без FIFO-starvation).
        using var wsB = await ConnectAsync(stack.Port, [tokenB, RealProto]);
        await CompletePopAsync(wsB, keyB);
        using var response = await SendAndReceiveResponseAsync(
            wsB, SendFrame(99, account: "+15551230003"));

        Assert.False(response.RootElement.TryGetProperty("error", out var err),
            "floor юзера Б з'їдений: " + (response.RootElement.TryGetProperty("error", out err) ? err.ToString() : ""));
    }

    // ---------------------------------------------------------------- 5.3 (W13)

    [Fact]
    public async Task Budget_SurvivesRestart_ForfeitsRemainder_NeverOversends()
    {
        var config = new Dictionary<string, string?>
        {
            ["Server:Admission:AggregateBudgetPerWindow"] = "2",
            ["Server:Admission:ReserveBlockSize"] = "2",
            ["Server:Admission:PerUserFloorPerWindow"] = "1",
        };

        string token;
        ECDsa key;
        using (var first = BuildStack(config))
        {
            key = first.EnrollIdentity("dod-w13", out token);
            using var ws = await ConnectAsync(first.Port, [token, RealProto]);
            await CompletePopAsync(ws, key);

            // 1 send: durable-блок K=2 зарезервовано, 1 юніт витрачено, 1 лишився в пам'яті.
            using var ok = await SendAndReceiveResponseAsync(ws, SendFrame(1));
            Assert.False(ok.RootElement.TryGetProperty("error", out _));
        }

        // «Рестарт» (новий стек на тому ж durable.db, те саме вікно): невитрачений юніт блоку
        // форфейтнуто — blocks×K=2 ≥ aggregate=2 → нових блоків нема, send DENY. Over-send
        // неможливий (навіть попри те, що фактично відправлено лише 1 із 2).
        using var second = BuildStack(config, reuseDataDir: true);
        using var ws2 = await ConnectAsync(second.Port, [token, RealProto]);
        await CompletePopAsync(ws2, key);

        using var denied = await SendAndReceiveResponseAsync(ws2, SendFrame(2));
        Assert.True(denied.RootElement.TryGetProperty("error", out var error));
        Assert.Equal(-32005, error.GetProperty("code").GetInt32());
        Assert.Equal(1, second.BackendCalls == 0 ? 1 : 0); // бекенд другого стека не викликався
    }

    // ---------------------------------------------------------------- 5.4 (G6)

    [Fact]
    public async Task SlowSendFlood_GateHolds_PendingBounded()
    {
        using var stack = BuildStack(new()
        {
            ["Server:Admission:SignalCliConcurrencyLimit"] = "1",
            ["Server:Admission:AggregateBudgetPerWindow"] = "100",
        }, holdBackend: true);
        var key = stack.EnrollIdentity("dod-g6", out var token);

        // Конект 1 займає єдиний слот гейта повільним send-ом (бекенд тримаємо відкритим TCS-ом).
        using var slow = await ConnectAsync(stack.Port, [token, RealProto]);
        await CompletePopAsync(slow, key);
        var slowTask = SendAndReceiveResponseAsync(slow, SendFrame(1));
        await stack.WaitBackendEnteredAsync(); // бекенд реально зайнятий

        // Конект 2: send при зайнятому гейті → миттєвий -32005 (pending не росте, G6).
        using var fast = await ConnectAsync(stack.Port, [token, RealProto]);
        await CompletePopAsync(fast, key);
        using var refused = await SendAndReceiveResponseAsync(fast, SendFrame(2));
        Assert.True(refused.RootElement.TryGetProperty("error", out var error));
        Assert.Equal(-32005, error.GetProperty("code").GetInt32());

        // Звільняємо бекенд — повільний send завершується успішно (нічого не зависло).
        stack.ReleaseBackend();
        using var slowResult = await slowTask;
        Assert.False(slowResult.RootElement.TryGetProperty("error", out _));
        Assert.Equal(1, stack.BackendCalls);
    }

    // ---------------------------------------------------------------- 5.5

    [Fact]
    public async Task OversizedMessage_ClosesMessageTooBig()
    {
        using var stack = BuildStack([]);
        var key = stack.EnrollIdentity("dod-64k", out var token);

        using var ws = await ConnectAsync(stack.Port, [token, RealProto]);
        await CompletePopAsync(ws, key);

        // Кадр > 64KB (деплой-дефолт V3) → сесія закривається MessageTooBig (1009).
        var huge = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"pad\":\"" +
                   new string('a', 70 * 1024) + "\"}}";
        await SendJsonAsync(ws, huge);

        var (code, _) = await ReceiveCloseAsync(ws);
        Assert.Equal((int)WebSocketCloseStatus.MessageTooBig, code);
    }

    [Fact]
    public async Task UnauthenticatedSocket_ClosedByAuthTimeout()
    {
        using var stack = BuildStack(new() { ["Server:Auth:AuthTimeoutSeconds"] = "3" });

        // Браузерний конект без токена, authenticate не шлемо → 4408 auth-timeout (~3с).
        using var ws = await ConnectAsync(stack.Port, [RealProto], origin: ServerStack.AllowedOrigin);
        var (code, reason) = await ReceiveCloseAsync(ws, timeoutSeconds: 15);

        Assert.Equal(4408, code);
        Assert.Equal("auth-timeout", reason);
    }

    // ---------------------------------------------------------------- 5.6 (S9)

    [Fact]
    public async Task QuotaExceeded_InBandRetryAfter_VisibleToBrowserClient()
    {
        using var stack = BuildStack(new()
        {
            ["Server:Admission:AggregateBudgetPerWindow"] = "1",
            ["Server:Admission:ReserveBlockSize"] = "1",
            ["Server:Admission:PerUserFloorPerWindow"] = "1",
        });
        var key = stack.EnrollIdentity("dod-s9", out var token);

        // Браузерний клієнт (Origin): перший send з'їдає бюджет, другий отримує -32005 з
        // data.retry_after (int, сек) У ВІДПОВІДІ — in-band, без HTTP-статусів (S9).
        using var ws = await ConnectAsync(stack.Port, [token, RealProto], origin: ServerStack.AllowedOrigin);
        await CompletePopAsync(ws, key);

        using var ok = await SendAndReceiveResponseAsync(ws, SendFrame(1));
        Assert.False(ok.RootElement.TryGetProperty("error", out _));

        using var denied = await SendAndReceiveResponseAsync(ws, SendFrame(2));
        var error = denied.RootElement.GetProperty("error");
        Assert.Equal(-32005, error.GetProperty("code").GetInt32());
        var retryAfter = error.GetProperty("data").GetProperty("retry_after").GetInt32();
        Assert.InRange(retryAfter, 1, 3600);
    }

    [Fact]
    public async Task MessageRateExceeded_Closes4429_WithMachineReadableReason()
    {
        using var stack = BuildStack(new()
        {
            ["Server:Admission:MessagesPerMinutePerConnection"] = "1",
            ["Server:Admission:AggregateBudgetPerWindow"] = "100",
        });
        var key = stack.EnrollIdentity("dod-4429", out var token);

        using var ws = await ConnectAsync(stack.Port, [token, RealProto], origin: ServerStack.AllowedOrigin);
        await CompletePopAsync(ws, key);

        // Ємність token-bucket = 1: перший активний кадр проходить, другий → close 4429
        // reason `message-rate:<sec>` — браузерний клієнт бачить причину in-band (S9).
        using var first = await SendAndReceiveResponseAsync(ws, SendFrame(1));
        await SendJsonAsync(ws, SendFrame(2));

        var (code, reason) = await ReceiveCloseAsync(ws);
        Assert.Equal(4429, code);
        Assert.NotNull(reason);
        Assert.StartsWith("message-rate:", reason);
        Assert.True(int.TryParse(reason!["message-rate:".Length..], out var sec) && sec >= 1,
            "reason має нести машиночитні секунди: " + reason);
    }

    // ================================================================ стек

    private ServerStack BuildStack(
        Dictionary<string, string?> overrides, bool holdBackend = false, bool reuseDataDir = false)
    {
        var stack = ServerStack.Start(_dataDir, overrides, holdBackend, reuseDataDir);
        _stacks.Add(stack);
        return stack;
    }

    public void Dispose()
    {
        foreach (var stack in _stacks)
            stack.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort прибирання темп-каталогу
        }
    }

    /// <summary>Живий сервер-стек: реальні auth/admission/chokepoint, замоканий send-бекенд.</summary>
    private sealed class ServerStack : IDisposable
    {
        public const string AllowedOrigin = "chrome-extension://dodtest";

        private ServiceProvider _services = null!;
        private SignalRpcServer _server = null!;
        private DurableStore _store = null!;
        private DeviceEnrollmentService _enrollment = null!;
        private int _backendCalls;
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Logs = new();
        private TaskCompletionSource? _backendHold;
        private readonly SemaphoreSlim _backendEntered = new(0);

        public int Port { get; private set; }

        public int BackendCalls => Volatile.Read(ref _backendCalls);

        public static ServerStack Start(
            string dataDir, Dictionary<string, string?> overrides, bool holdBackend, bool reuseDataDir)
        {
            var stack = new ServerStack();
            if (!reuseDataDir)
            {
                Directory.CreateDirectory(Path.Combine(dataDir, "secrets"));
                File.WriteAllText(
                    Path.Combine(dataDir, "secrets", "pepper_token"),
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                File.WriteAllText(
                    Path.Combine(dataDir, "secrets", "pepper_abuse"),
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            }

            var settings = new Dictionary<string, string?>
            {
                ["Server:Auth:Enabled"] = "true",
                ["Server:Auth:AuthTimeoutSeconds"] = "15",
                ["Server:Auth:MaxUnauthenticatedPerIp"] = "64",
                ["Server:Auth:AllowedOrigins:0"] = AllowedOrigin,
                ["Server:Admission:Enabled"] = "true",
            };
            foreach (var (k, v) in overrides)
                settings[k] = v;

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

            if (holdBackend)
                stack._backendHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Замоканий бекенд: рахує реальні виклики; за потреби «повільний» (тримається TCS-ом, G6).
            var backend = new Mock<ISignalMessageRpc>();
            backend
                .Setup(m => m.SendTextMessage(
                    It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    Interlocked.Increment(ref stack._backendCalls);
                    stack._backendEntered.Release();
                    if (stack._backendHold is { } hold)
                        await hold.Task;
                    return (SendMessageResponse)null!;
                });

            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(new QueueLoggerProvider(stack.Logs)));
            services.Configure<PersistenceOptions>(o => o.DataDirectory = dataDir);

            stack._store = new DurableStore(
                Path.Combine(dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
            stack._store.Initialize();
            services.AddSingleton<IDurableStore>(stack._store);

            services.AddAuthnCore(configuration);
            services.AddAdmissionCore(configuration);
            services.AddSingleton(new JsonRpcServerConfig { MaxMessageSizeBytes = 65536 });

            // Реальний реєстр + політики; RPC-поверхня = лише замоканий message-бекенд.
            services.AddSingleton<global::WsRpcServer.Services.IRpcServiceRegistry, RpcServiceRegistry>();
            services.AddSingleton<IRpcPolicyRegistry, SignalRpcPolicyRegistry>();
            services.AddSingleton(backend.Object);
            // Реєстр дискаверить УСІ IRpcService-інтерфейси збірки — решта поверхні теж має
            // резолвитись (порожні моки; бізнес-виклики у ці тести не йдуть).
            services.AddSingleton(Mock.Of<ISignalAccountsRpc>());
            services.AddSingleton(Mock.Of<ISignalDevicesRpc>());
            services.AddSingleton(Mock.Of<ISignalGroupsRpc>());
            services.AddSingleton(Mock.Of<ISystemRpc>());
            services.AddSingleton(Mock.Of<IEventProcessor>());

            stack._services = services.BuildServiceProvider();
            stack._enrollment = stack._services.GetRequiredService<DeviceEnrollmentService>();

            stack.Port = GetFreePort();
            stack._server = new SignalRpcServer(
                IPAddress.Loopback, stack.Port, stack._services,
                stack._services.GetRequiredService<ILogger<SignalRpcServer>>());
            Assert.True(stack._server.Start());
            return stack;
        }

        // Сідить identity (+binding-акаунт у linkedAccounts) і реєструє device-ключ enrollment-сервісом.
        public ECDsa EnrollIdentity(string id, out string token, string account = Account)
        {
            _store.UpsertIdentity(new IdentityRecord(id, "user", [account], DateTimeOffset.UtcNow));
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            (token, _) = _enrollment.Enroll(id, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
            return key;
        }

        public Task WaitBackendEnteredAsync() => _backendEntered.WaitAsync(TimeSpan.FromSeconds(10));

        public void ReleaseBackend() => _backendHold?.TrySetResult();

        public void Dispose()
        {
            ReleaseBackend();
            _server.Stop();
            _server.Dispose();
            _services.Dispose();
            _store.Dispose();
        }
    }

    /// <summary>Мінімальний провайдер: усі лог-рядки в чергу (діагностика живого стека).</summary>
    private sealed class QueueLoggerProvider(System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new QueueLogger(lines, categoryName);

        public void Dispose()
        {
        }

        private sealed class QueueLogger(System.Collections.Concurrent.ConcurrentQueue<string> lines, string cat) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex,
                Func<TState, Exception?, string> f) =>
                lines.Enqueue($"[{l}] {cat}: {f(s, ex)}{(ex is null ? "" : " EX: " + ex)}");
        }
    }

    // ================================================================ клієнт-хелпери

    private static string SendFrame(int id, string account = Account) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"sendTextMessage\",\"params\":{" +
        "\"account\":\"" + account + "\",\"recipients\":[\"" + Recipient + "\"],\"message\":\"dod\"}}";

    private static async Task<ClientWebSocket> ConnectAsync(int port, string[] subprotocols, string? origin = null)
    {
        var ws = new ClientWebSocket();
        foreach (var proto in subprotocols)
            ws.Options.AddSubProtocol(proto);
        if (origin is not null)
            ws.Options.SetRequestHeader("Origin", origin);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), Cts().Token);
        return ws;
    }

    private static CancellationTokenSource Cts(int seconds = 10) => new(TimeSpan.FromSeconds(seconds));

    private static async Task SendJsonAsync(ClientWebSocket ws, string json) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, Cts().Token);

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket ws)
    {
        var buffer = new byte[128 * 1024];
        var result = await ws.ReceiveAsync(buffer, Cts(30).Token);
        // Pre-auth кадри сесія шле Text-ом, а StreamJsonRpc-трафік WebSocketMessageHandler фреймворку
        // шле Binary-кадрами (SendBinaryDataAsync) — приймаємо обидва, як реальний WHATWG-клієнт.
        Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
        return JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, 0, result.Count).ToArray());
    }

    private static async Task<JsonDocument> SendAndReceiveResponseAsync(ClientWebSocket ws, string frame)
    {
        await SendJsonAsync(ws, frame);
        return await ReceiveJsonAsync(ws);
    }

    private static async Task<(string Nonce, string ConnId)> ReceivePopChallengeAsync(ClientWebSocket ws)
    {
        using var doc = await ReceiveJsonAsync(ws);
        Assert.Equal("pop.challenge", doc.RootElement.GetProperty("method").GetString());
        var p = doc.RootElement.GetProperty("params");
        return (p.GetProperty("nonce").GetString()!, p.GetProperty("connId").GetString()!);
    }

    private static async Task CompletePopAsync(ClientWebSocket ws, ECDsa key)
    {
        var (nonce, connId) = await ReceivePopChallengeAsync(ws);
        var signature = Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes(nonce + connId), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        await SendJsonAsync(ws,
            "{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"pop.prove\",\"params\":{\"signature\":\"" +
            signature + "\"}}");
        using var ack = await ReceiveJsonAsync(ws);
        Assert.Equal("ok", ack.RootElement.GetProperty("result").GetProperty("status").GetString());
    }

    private static async Task<(int Code, string? Reason)> ReceiveCloseAsync(
        ClientWebSocket ws, int timeoutSeconds = 10)
    {
        var buffer = new byte[8192];
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, Cts(timeoutSeconds).Token);
            if (result.MessageType == WebSocketMessageType.Close)
                return ((int)(ws.CloseStatus ?? 0), ws.CloseStatusDescription);
        }
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

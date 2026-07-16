using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Extensions;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Services;
using WsRpcServer.Core;
using WsRpcServer.Events;
using WsRpcServer.Services;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// DoD-тести секції 4 (add-authn-token-pop) через РЕАЛЬНИЙ шлях виконання: живий
/// <see cref="SignalRpcServer"/> (NetCoreServer) на вільному порту + справжній
/// <see cref="ClientWebSocket"/>. Реальні TokenService/DurableStore/PoP/стан-машина; замокані лише
/// RPC-реєстр і event-процесор (upstream signal-cli не бере участі в authn-шляхах).
/// 4.1 expired → auth-відмова без бізнес-логіки; 4.2 echo-leak/C4; 4.3 PoP replay/relay (G8);
/// 4.4 revoke-каскад рве живий конект; 4.5 токен не в логах; 4.6 T4 4401+reason vs HTTP 401;
/// 4.7 T5 renewal zero-overlap.
/// </summary>
public sealed class AuthnHandshakeDodTests : IAsyncLifetime
{
    private const string RealProto = "jsonrpc.dod";
    private const string AllowedOrigin = "chrome-extension://dodtest";

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-authn-dod-" + Guid.NewGuid().ToString("N"));

    private readonly CapturingLoggerProvider _logCapture = new();
    private readonly Mock<IRpcServiceRegistry> _registryMock = new();

    private ServiceProvider _services = null!;
    private SignalRpcServer _server = null!;
    private DurableStore _store = null!;
    private TokenService _tokenService = null!;
    private DeviceEnrollmentService _enrollment = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_dataDir, "secrets"));
        // Пеппер пишемо напряму (без повного SecureDeploymentHostedService — DoD цього файлу
        // цілиться в authn-шлях; провіжн секретів покритий DoD change 2).
        await File.WriteAllTextAsync(
            Path.Combine(_dataDir, "secrets", "pepper_token"),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Server:Auth:Enabled"] = "true",
                ["Server:Auth:TokenTtlHours"] = "12",
                ["Server:Auth:AuthTimeoutSeconds"] = "15",
                ["Server:Auth:MaxUnauthenticatedPerIp"] = "32",
                ["Server:Auth:RenewalPerIdentityPerHour"] = "4",
                ["Server:Auth:AllowedOrigins:0"] = AllowedOrigin,
            }).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(_logCapture).SetMinimumLevel(LogLevel.Debug));
        services.Configure<PersistenceOptions>(o => o.DataDirectory = _dataDir);

        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DurableStore>.Instance);
        _store.Initialize();
        services.AddSingleton<IDurableStore>(_store);

        // Реальний authn-стек (пеппер/токени/limiter/broadcaster/enrollment) — як у проді.
        services.AddAuthnCore(configuration);

        // Framework-конфіг сесії; host/port сюди не йдуть (сервер конструюємо вручну нижче).
        services.AddSingleton(new JsonRpcServerConfig());

        // Поза authn-скоупом: RPC-поверхня і події — моки (жоден бізнес-RPC не має виконатись).
        services.AddSingleton(_registryMock.Object);
        services.AddSingleton(Mock.Of<IEventProcessor>());
        services.AddSingleton<IRpcPolicyRegistry, SignalRpcPolicyRegistry>();

        _services = services.BuildServiceProvider();
        _tokenService = _services.GetRequiredService<TokenService>();
        _enrollment = _services.GetRequiredService<DeviceEnrollmentService>();

        _port = GetFreePort();
        _server = new SignalRpcServer(
            IPAddress.Loopback, _port, _services,
            _services.GetRequiredService<ILogger<SignalRpcServer>>());
        Assert.True(_server.Start());
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

    // ---------------------------------------------------------------- 4.1 / 4.6 (T4)

    [Fact]
    public async Task ExpiredToken_OnUpgrade_Closes4401Expired_WithoutBusinessLogic()
    {
        var identity = SeedIdentity("dod-expired");
        var (token, _) = _tokenService.IssueToken(identity, TimeSpan.FromMilliseconds(1));
        await Task.Delay(100);

        using var ws = await ConnectAsync([token, RealProto]);
        var (code, reason) = await ReceiveCloseAsync(ws);

        // T4: браузер бачить машиночитний close-code, а не generic 1006/мережевий фейл.
        Assert.Equal(4401, code);
        Assert.Equal("expired", reason);
        // 4.1: жодної бізнес-логіки — RPC-стек навіть не реєструвався для цієї сесії.
        _registryMock.Verify(
            r => r.RegisterServices(It.IsAny<StreamJsonRpc.JsonRpc>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task NoToken_NoOrigin_Rejected401_BeforeUpgrade()
    {
        // Не-браузерний клієнт без токена → голий HTTP 401 ДО апгрейду: ConnectAsync фейлиться,
        // WebSocket-з'єднання не встановлюється взагалі.
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol(RealProto);

        await Assert.ThrowsAsync<WebSocketException>(
            () => ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/"), CancellationToken.None));
    }

    // ---------------------------------------------------------------- 4.2 (C4)

    [Fact]
    public async Task BrowserClient_TokenPlusRealProto_ReachesOpen_EchoesOnlyRealProto()
    {
        var identity = SeedIdentity("dod-echo");
        var key = EnrollDevice(identity, out var token);

        using var ws = await ConnectAsync([token, RealProto], origin: AllowedOrigin);

        // Echo-leak: negotiated субпротокол — РІВНО real-proto, токен ніколи не повертається.
        Assert.Equal(RealProto, ws.SubProtocol);

        // Позитивний C4: з'єднання досягає open і доходить до Active через повний PoP.
        await CompletePopAsync(ws, key);
        Assert.Equal(WebSocketState.Open, ws.State);
    }

    // ---------------------------------------------------------------- 4.3 (G8)

    [Fact]
    public async Task StolenToken_WithoutDeviceKey_PopFails_NoRpcExecuted()
    {
        var identity = SeedIdentity("dod-stolen");
        EnrollDevice(identity, out var token);

        // «Крадій» має токен, але НЕ має device-ключа: на challenge відповідає сміттям.
        using var ws = await ConnectAsync([token, RealProto]);
        _ = await ReceivePopChallengeAsync(ws);
        await SendJsonAsync(ws, """{"jsonrpc":"2.0","id":7,"method":"pop.prove","params":{"signature":"Z2FyYmFnZQ=="}}""");

        var (code, reason) = await ReceiveCloseAsync(ws);
        Assert.Equal(4401, code);
        Assert.Equal("pop-failed", reason);
        _registryMock.Verify(
            r => r.RegisterServices(It.IsAny<StreamJsonRpc.JsonRpc>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task RelayedSignature_FromAnotherConnection_PopFails()
    {
        var identity = SeedIdentity("dod-relay");
        var key = EnrollDevice(identity, out var token);

        // Конект A: чесний challenge, чесний підпис — але НЕ відправляємо його на A.
        using var wsA = await ConnectAsync([token, RealProto]);
        var challengeA = await ReceivePopChallengeAsync(wsA);
        var signatureForA = SignChallenge(key, challengeA);

        // Конект B (той самий токен): свій challenge (інший nonce + connId). Релей підпису з A → відмова:
        // pre-image включає connId, тож підпис bound до з'єднання (G8).
        using var wsB = await ConnectAsync([token, RealProto]);
        _ = await ReceivePopChallengeAsync(wsB);
        await SendJsonAsync(wsB, PopProveFrame(8, signatureForA));

        var (code, reason) = await ReceiveCloseAsync(wsB);
        Assert.Equal(4401, code);
        Assert.Equal("pop-failed", reason);
    }

    // ---------------------------------------------------------------- 4.4

    [Fact]
    public async Task RevokeIdentity_KillsLiveConnection_AndDeviceSecret()
    {
        var identity = SeedIdentity("dod-revoke");
        var key = EnrollDevice(identity, out var token);

        using var ws = await ConnectAsync([token, RealProto]);
        await CompletePopAsync(ws, key);

        // Активний конект + revoke identity → сесія рветься in-process close 4401 revoked (1.4),
        // device-секрет мертвий у сторі (каскад).
        _tokenService.RevokeIdentity(identity);

        var (code, reason) = await ReceiveCloseAsync(ws);
        Assert.Equal(4401, code);
        Assert.Equal("revoked", reason);

        var secret = _store.GetDeviceSecret(identity);
        Assert.NotNull(secret);
        Assert.True(secret!.IsRevoked);
    }

    // ---------------------------------------------------------------- 4.6 (T4, браузерний шлях)

    [Fact]
    public async Task BrowserClient_ExpiredToken_SeesCloseCode_NotNetworkFailure()
    {
        var identity = SeedIdentity("dod-t4");
        var (token, _) = _tokenService.IssueToken(identity, TimeSpan.FromMilliseconds(1));
        await Task.Delay(100);

        // Браузерний клієнт (Origin у allowlist): upgrade ЗАВЕРШУЄТЬСЯ (open досяжний), потім
        // close 4401 `expired` — на відміну від мережевого фейлу, де ConnectAsync кинув би виняток.
        using var ws = await ConnectAsync([token, RealProto], origin: AllowedOrigin);
        var (code, reason) = await ReceiveCloseAsync(ws);

        Assert.Equal(4401, code);
        Assert.Equal("expired", reason);
    }

    // ---------------------------------------------------------------- 4.7 (T5)

    [Fact]
    public async Task ExpiredToken_WithPop_RenewsToken_ZeroOverlap()
    {
        var identity = SeedIdentity("dod-renew");
        var key = EnrollDevice(identity, out var initialToken);

        // Протухлий (НЕ revoked) токен тієї ж identity.
        var (expiredToken, expiredRecord) = _tokenService.IssueToken(identity, TimeSpan.FromMilliseconds(1));
        await Task.Delay(100);

        // Браузерний fallback: конект БЕЗ токена → authenticate(expired) → challenge → PoP → новий токен.
        using var ws = await ConnectAsync([RealProto], origin: AllowedOrigin);
        await SendJsonAsync(ws, AuthenticateFrame(1, expiredToken));
        var challenge = await ReceivePopChallengeAsync(ws);
        await SendJsonAsync(ws, PopProveFrame(2, SignChallenge(key, challenge)));

        var ack = await ReceiveJsonAsync(ws);
        var result = ack.RootElement.GetProperty("result");
        Assert.Equal("ok", result.GetProperty("status").GetString());
        var renewedToken = result.GetProperty("token").GetString();
        Assert.NotNull(renewedToken);
        Assert.NotEqual(expiredToken, renewedToken);

        // Zero-overlap (W21): старий токен revoked у сторі, новий — живий і РОБОЧИЙ (повний цикл).
        var oldRecord = _store.GetToken(expiredRecord.TokenHash);
        Assert.True(oldRecord!.IsRevoked);

        using var ws2 = await ConnectAsync([renewedToken!, RealProto]);
        await CompletePopAsync(ws2, key);
        Assert.Equal(WebSocketState.Open, ws2.State);
    }

    [Fact]
    public async Task RevokedToken_WithValidPop_AlwaysRefused()
    {
        var identity = SeedIdentity("dod-revoked-pop");
        EnrollDevice(identity, out var token);
        _tokenService.RevokeIdentity(identity);

        // Revoked перемагає завжди: навіть валідний device-ключ не дає renewal — challenge не видається.
        using var ws = await ConnectAsync([RealProto], origin: AllowedOrigin);
        await SendJsonAsync(ws, AuthenticateFrame(1, token));

        var (code, reason) = await ReceiveCloseAsync(ws);
        Assert.Equal(4401, code);
        Assert.Equal("revoked", reason);
    }

    // ---------------------------------------------------------------- 4.5

    [Fact]
    public async Task PlaintextTokens_NeverAppearInLogs()
    {
        var identity = SeedIdentity("dod-redact");
        var key = EnrollDevice(identity, out var token);

        // Повний happy-path (upgrade-токен + PoP + активація) — найбалакучіший шлях.
        using (var ws = await ConnectAsync([token, RealProto], origin: AllowedOrigin))
        {
            await CompletePopAsync(ws, key);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }

        // Redact: 43-символьний payload-сегмент токена не сміє з'явитись у жодному лог-рядку
        // (перевіряємо сегмент, а не весь токен — ловить і часткові витоки формату).
        // Позиційно, НЕ Split-ом: base64url-алфавіт сам містить '_' (та сама пастка, що у TryParse).
        var payloadSegment = token.Substring(token.IndexOf('_', TokenFormat.Prefix.Length) + 1, 43);
        Assert.True(payloadSegment.Length >= 40, "self-check формату");
        foreach (var line in _logCapture.Lines)
            Assert.DoesNotContain(payloadSegment, line, StringComparison.Ordinal);
    }

    // ================================================================ інфраструктура

    private string SeedIdentity(string id)
    {
        _store.UpsertIdentity(new IdentityRecord(id, "user", ["+15551230001"], DateTimeOffset.UtcNow));
        return id;
    }

    // Реєструє device-ключ через реальний enrollment-сервіс і повертає ECDsa + перший токен.
    private ECDsa EnrollDevice(string identityId, out string token)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        (token, _) = _enrollment.Enroll(identityId, spki);
        return key;
    }

    private async Task<ClientWebSocket> ConnectAsync(string[] subprotocols, string? origin = null)
    {
        var ws = new ClientWebSocket();
        foreach (var proto in subprotocols)
            ws.Options.AddSubProtocol(proto);
        if (origin is not null)
            ws.Options.SetRequestHeader("Origin", origin);
        ws.Options.CollectHttpResponseDetails = true;

        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/"), Cts().Token);
        return ws;
    }

    private static CancellationTokenSource Cts() => new(TimeSpan.FromSeconds(10));

    // JSON-кадри pre-auth протоколу: конкатенація замість raw-інтерполяції (CS9007 на `}}`+`}}`).
    private static string AuthenticateFrame(int id, string token) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"authenticate\",\"params\":{\"token\":\"" + token + "\"}}";

    private static string PopProveFrame(int id, string signature) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"pop.prove\",\"params\":{\"signature\":\"" + signature + "\"}}";

    private static async Task SendJsonAsync(ClientWebSocket ws, string json) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, Cts().Token);

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        var result = await ws.ReceiveAsync(buffer, Cts().Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, 0, result.Count));
    }

    private static async Task<(string Nonce, string ConnId)> ReceivePopChallengeAsync(ClientWebSocket ws)
    {
        using var doc = await ReceiveJsonAsync(ws);
        Assert.Equal("pop.challenge", doc.RootElement.GetProperty("method").GetString());
        var p = doc.RootElement.GetProperty("params");
        return (p.GetProperty("nonce").GetString()!, p.GetProperty("connId").GetString()!);
    }

    // Підпис challenge device-ключем: SHA-256 над UTF8(nonce+connId), формат IEEE P1363 (WebCrypto).
    private static string SignChallenge(ECDsa key, (string Nonce, string ConnId) challenge)
    {
        var preImage = Encoding.UTF8.GetBytes(challenge.Nonce + challenge.ConnId);
        return Convert.ToBase64String(key.SignData(
            preImage, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private static async Task CompletePopAsync(ClientWebSocket ws, ECDsa key)
    {
        var challenge = await ReceivePopChallengeAsync(ws);
        await SendJsonAsync(ws, PopProveFrame(99, SignChallenge(key, challenge)));
        using var ack = await ReceiveJsonAsync(ws);
        Assert.Equal("ok", ack.RootElement.GetProperty("result").GetProperty("status").GetString());
    }

    // Чекає close-frame; повертає (code, reason). Сервер міг закрити ще до/під час нашого read.
    private static async Task<(int Code, string? Reason)> ReceiveCloseAsync(ClientWebSocket ws)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, Cts().Token);
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

    /// <summary>Потокобезпечний колектор усіх лог-рядків (для redact-перевірки 4.5).</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_lines);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lines.Enqueue(formatter(state, exception) + (exception is null ? "" : exception.ToString()));
            }
        }
    }
}

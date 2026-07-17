using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Accounts;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Interfaces;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Services;
using StreamJsonRpc;
using WsRpcServer.Core;
using WsRpcServer.Events;
using WsRpcServer.Security;
using WsRpcServer.Services;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// DoD секції 4 (add-invites-admin) через РЕАЛЬНИЙ шлях: живий <see cref="SecureAdminRpcServer"/> над
/// провіжненими секретами + справжній <see cref="ClientWebSocket"/> з admin client-cert; реальні
/// bootstrap/chokepoint/admin-адаптер/InviteService/AuditLog. 4.1 G12 cap; 4.2 G5 (cert не в логах);
/// 4.3 admin-метод — non-admin cert (валідний ланцюг, не мапнутий) → відмова, admin → проходить;
/// 4.4 G10 legacy-акаунт видно лише admin; 4.5 audit-події лягають (tamper-evident chain).
/// </summary>
public sealed class AdminMtlsDodTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-admin-dod-" + Guid.NewGuid().ToString("N"));

    private readonly string _secretsDir;
    private readonly ProvisionedSecrets _secrets;
    private readonly DurableStore _store;
    private readonly AuditLogService _auditLog;
    private readonly Mock<ISignalAccounts> _accountsMock = new();
    private readonly ServiceProvider _provider;
    private readonly SecureAdminRpcServer _server;
    private readonly int _port;
    private readonly List<IDisposable> _cleanup = [];

    public AdminMtlsDodTests()
    {
        Directory.CreateDirectory(_dataDir);
        _secrets = new SecretMaterialProvisioner(NullLogger<SecretMaterialProvisioner>.Instance)
            .Provision(_dataDir, hostname: null);
        _secretsDir = Path.Combine(_dataDir, "secrets");
        // Пеппери потрібні InviteService/AuditLog (abuse-пеппер) — SecretMaterialProvisioner їх уже написав.

        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton(new JsonRpcServerConfig { Host = "127.0.0.1", Port = 0 });
        services.Configure<PersistenceOptions>(o => o.DataDirectory = _dataDir);
        services.Configure<AdminOptions>(o => { });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDurableStore>(_store);
        services.AddSingleton<IAbusePepperProvider, AbusePepperProvider>();
        services.AddSingleton<IPepperProvider, FilePepperProvider>();
        services.AddSingleton<InviteService>();
        services.AddSingleton<InviteRedemptionRateLimiter>();
        services.AddSingleton<TokenService>();
        services.AddSingleton<RevocationBroadcaster>();
        services.AddSingleton<AuditLogService>();
        services.AddSingleton(_accountsMock.Object);
        services.AddSingleton<IRpcPolicyRegistry, SignalRpcPolicyRegistry>();
        services.AddSingleton<IEventProcessor, NoopEventProcessor>();
        // Реальна admin-поверхня + accounts-адаптер (з admin-залежностями для G10) через реальний реєстр.
        services.AddScoped<ISignalAdminRpc, SignalAdminRpcAdapter>();
        services.AddScoped<ISignalAccountsRpc>(sp => new SignalAccountsRpcAdapter(
            _accountsMock.Object,
            sp.GetRequiredService<ILogger<SignalAccountsRpcAdapter>>(),
            sp.GetRequiredService<IDurableStore>(),
            sp.GetRequiredService<AuditLogService>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ISystemRpc, SystemRpcAdapter>();
        services.AddSingleton<IRpcServiceRegistry, RpcServiceRegistry>();
        _provider = services.BuildServiceProvider();
        _auditLog = _provider.GetRequiredService<AuditLogService>();

        // Bootstrap-шлях: admin-identity + admin_credentials mapping (SPKI admin-cert-а з тому).
        _store.UpsertIdentity(new IdentityRecord("admin", "admin", [], DateTimeOffset.UtcNow));
        var adminSpki = NodeIdentity.ComputeSpkiThumbprint(
            X509Certificate2.CreateFromPem(File.ReadAllText(_secrets.AdminClientCertificatePath)));
        _store.UpsertAdminCredential(new AdminCredentialRecord(adminSpki, "admin", Revoked: false, DateTimeOffset.UtcNow));

        var transport = BuildTransport();
        _port = FreeLoopbackPort();
        _server = ActivatorUtilities.CreateInstance<SecureAdminRpcServer>(
            _provider, transport, IPAddress.Loopback, _port);
        Assert.True(_server.Start());
    }

    // ---------------------------------------------------------------- 4.3 + 4.5 повний admin-флоу

    [Fact]
    public async Task AdminCert_CreateInvite_OverMtls_MintsCode_AndAudits()
    {
        using var adminCert = LoadClientCertificate(
            Path.Combine(_secretsDir, "admin-client.crt"), Path.Combine(_secretsDir, "admin-client.key"));
        using var ws = await ConnectAsync(adminCert);

        var response = await CallAsync(ws, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"createInvite\",\"params\":{}}");
        var result = response.RootElement.GetProperty("result");
        var code = result.GetProperty("code").GetString();
        Assert.False(string.IsNullOrWhiteSpace(code));

        // Інвайт персистнутий (at-rest hash), і подія лягла у tamper-evident audit-trail (4.5).
        var invite = _store.GetInvite(InviteService.ComputeCodeHash(code!));
        Assert.NotNull(invite);
        Assert.Equal("admin", invite!.CreatedBy);

        var events = _store.GetAuditEntries();
        Assert.Contains(events, e => e.EventType == "invite_created");
        Assert.True(_auditLog.VerifyChain(), "audit hash-chain недійсний після createInvite");
    }

    [Fact]
    public async Task ValidCaCert_NotAnAdminCredential_IsRefused_NoAdminMethod()
    {
        // Client-cert від НАШОГО CA (ланцюг валідний), але його SPKI НЕ мапнутий в admin_credentials
        // → AdminPrincipalResolver повертає null → сесія закривається, жоден admin-метод не виконується.
        var now = DateTimeOffset.UtcNow;
        using var ca = X509Certificate2.CreateFromPemFile(
            Path.Combine(_secretsDir, "ca.crt"), Path.Combine(_secretsDir, "ca.key"));
        using var strangerLeaf = CertificateFactory.IssueAdminClientCertificate(ca, now.AddMinutes(-5), now.AddDays(30));
        using var stranger = X509CertificateLoader.LoadPkcs12(strangerLeaf.Export(X509ContentType.Pkcs12), null);

        // Handshake проходить (ланцюг до CA валідний), але сервер закриває сесію після resolve.
        using var ws = await ConnectAsync(stranger);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            // Спроба виклику на сесії, яку сервер закриває → recv/ send кине.
            await CallAsync(ws, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"createInvite\",\"params\":{}}");
        });

        // Жодного інвайту не створено (admin-метод не досяг адаптера).
        Assert.Empty(_store.GetAuditEntries());
    }

    [Fact]
    public async Task AdminCert_RevokeIdentity_OverMtls_Cascades_AndAudits()
    {
        // Готуємо identity з живим токеном + device-секретом.
        _store.UpsertIdentity(new IdentityRecord("victim", "user", [], DateTimeOffset.UtcNow));
        var tokenService = _provider.GetRequiredService<TokenService>();
        var (_, record) = tokenService.IssueToken("victim", TimeSpan.FromHours(1));

        using var adminCert = LoadClientCertificate(
            Path.Combine(_secretsDir, "admin-client.crt"), Path.Combine(_secretsDir, "admin-client.key"));
        using var ws = await ConnectAsync(adminCert);

        using var response = await CallAsync(ws,
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"revokeIdentity\",\"params\":{\"identityId\":\"victim\"}}");
        Assert.False(response.RootElement.TryGetProperty("error", out _));

        // Каскад: токен revoked; подія в audit-trail (chain дійсний).
        Assert.False(tokenService.IsLive(record.TokenHash));
        Assert.Contains(_store.GetAuditEntries(), e => e.EventType == "identity_revoked");
        Assert.True(_auditLog.VerifyChain());
    }

    // ---------------------------------------------------------------- 4.2 (G5)

    [Fact]
    public void Bootstrap_DoesNotLeakCertMaterial_ToLogs()
    {
        var capture = new ConcurrentQueue<string>();
        using var provider = new CapturingLoggerProvider(capture);
        var logger = provider.CreateLogger("boot");

        // Реальний bootstrap над провіжненими секретами (нова durable-БД, щоб bootstrap відпрацював свіжо).
        var bootStore = new DurableStore(Path.Combine(_dataDir, "boot.db"), NullLogger<DurableStore>.Instance);
        bootStore.Initialize();
        _cleanup.Add(bootStore);
        var boot = new AdminBootstrapService(bootStore, new TypedLogger<AdminBootstrapService>(logger));
        boot.Bootstrap(_secrets.AdminClientCertificatePath);

        var adminKeyPem = File.ReadAllText(Path.Combine(_secretsDir, "admin-client.key"));
        var keyBody = adminKeyPem.Replace("-----BEGIN PRIVATE KEY-----", "", StringComparison.Ordinal)
            .Replace("-----END PRIVATE KEY-----", "", StringComparison.Ordinal).Trim();

        // G5: жоден лог-рядок bootstrap не містить приватного ключа/PEM-тіла — лише факт провіжну.
        foreach (var line in capture)
        {
            Assert.DoesNotContain("PRIVATE KEY", line, StringComparison.Ordinal);
            Assert.DoesNotContain(keyBody[..40], line, StringComparison.Ordinal);
        }
        // Sanity: bootstrap реально щось залогував (щоб перевірка не була вакуумною).
        Assert.NotEmpty(capture);
    }

    // ---------------------------------------------------------------- 4.4 (G10)

    [Fact]
    public void G10_LegacyAccount_BoundToAdmin_OnAdminListAccounts_VisibleToAdmin()
    {
        // Демон повертає legacy-акаунт без binding у сторі.
        const string legacy = "+15550001111";
        _accountsMock.Setup(a => a.ListAccountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListAccountsResponse([new Account(legacy)]));

        Assert.Null(_store.GetAccountBinding(legacy)); // до огляду — orphan

        var admin = new SignalPrincipal("admin", isAdmin: true, hasFullAccess: false, [], []);
        using (SignalCallContext.Enter(admin))
        {
            using var scope = _provider.CreateScope();
            var adapter = scope.ServiceProvider.GetRequiredService<ISignalAccountsRpc>();
            _ = adapter.ListAccounts(CancellationToken.None).GetAwaiter().GetResult();
        }

        // G10: акаунт прив'язаний до admin-identity, IsPrivate=false (admin бачить; R3.6).
        var binding = _store.GetAccountBinding(legacy);
        Assert.NotNull(binding);
        Assert.Equal("admin", binding!.OwningIdentityId);
        Assert.False(binding.IsPrivate);
        Assert.Contains(_store.GetAuditEntries(), e => e.EventType == "admin_list_accounts");
    }

    // ---------------------------------------------------------------- 4.1 (G12)

    [Fact]
    public void G12_ParallelGuesses_DoNotBypassPerCodeCap()
    {
        var invites = _provider.GetRequiredService<InviteService>();
        var creation = invites.Create("admin", TimeSpan.FromMinutes(10), maxAttempts: 3);

        // 16 паралельних спроб редемпшину ОДНОГО коду: cap рахує кожну спробу (зокрема успішну),
        // consume — one-time, тож рівно одна виграє, і жодна гонка cap не обходить.
        var wins = 0;
        Parallel.For(0, 16, i =>
        {
            if (invites.TryRedeem(creation.Code, $"redeemer-{i}", DateTimeOffset.UtcNow) is not null)
                Interlocked.Increment(ref wins);
        });

        Assert.Equal(1, wins);
        // Другий валідний код тієї ж пачки спроб уже неможливий — інвайт consumed.
        Assert.Null(invites.TryRedeem(creation.Code, "late", DateTimeOffset.UtcNow));
    }

    // ================================================================ harness (mTLS, як у smoke-тесті)

    private SecureTransport BuildTransport()
    {
        using var pem = X509Certificate2.CreateFromPemFile(
            Path.Combine(_secretsDir, "server.crt"), Path.Combine(_secretsDir, "server.key"));
        var serverCert = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        var ca = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(_secretsDir, "ca.crt")));
        var tls = new TlsServerOptions
        {
            ServerCertificate = serverCert,
            ClientCertificateRequired = true,
            TrustedRoots = [ca],
            RevocationMode = X509RevocationMode.NoCheck,
            SslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        };
        var validator = new CustomRootTrustValidator(
            tls.TrustedRoots, spkiPins: null, tls.RevocationMode, NullLogger<CustomRootTrustValidator>.Instance);
        return SecureTransport.Create(tls, validator, new SpiffeNodeIdentityResolver(), NullLogger<SecureTransport>.Instance);
    }

    private async Task<ClientWebSocket> ConnectAsync(X509Certificate2 clientCertificate)
    {
        var caCert = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(_secretsDir, "ca.crt")));
        _cleanup.Add(caCert);
        var chainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        chainPolicy.CustomTrustStore.Add(caCert);
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateChainPolicy = chainPolicy,
                ClientCertificates = new X509CertificateCollection { clientCertificate },
            },
        };
        var invoker = new HttpMessageInvoker(handler);
        _cleanup.Add(invoker);
        var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{_port}/"), invoker, cts.Token);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private static async Task<JsonDocument> CallAsync(ClientWebSocket ws, string frame)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Binary, true, cts.Token);
        var buffer = new byte[16384];
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        if (result.MessageType == WebSocketMessageType.Close)
            throw new WebSocketException("сесія закрита сервером до відповіді");
        return JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, 0, result.Count).ToArray());
    }

    private static X509Certificate2 LoadClientCertificate(string certPath, string keyPath)
    {
        using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
    }

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        foreach (var d in _cleanup)
            d.Dispose();
        _server.Stop();
        _server.Dispose();
        _provider.Dispose();
        _store.Dispose();
        SqliteConnection.ClearAllPools();
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

    private sealed class NoopEventProcessor : IEventProcessor
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void RegisterClient(Guid clientId, Func<string, object[], Task> notificationHandler) { }
        public void UnregisterClient(Guid clientId) { }
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(lines);
        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex,
                Func<TState, Exception?, string> f) => lines.Enqueue(f(s, ex));
        }
    }

    // Обгортка нетипізованого ILogger у ILogger<T> (для AdminBootstrapService ctor).
    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex, Func<TState, Exception?, string> f) =>
            inner.Log(l, e, s, ex, f);
    }
}

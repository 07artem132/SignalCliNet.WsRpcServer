using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Deployment;
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
/// БАЗОВИЙ live-loopback smoke для mTLS admin-порту (add-invites-admin, task 2.3): справжній
/// <see cref="SecureAdminRpcServer"/> над реальними провіжненими секретами. Валідний admin client-cert →
/// конект проходить і <c>ping</c> round-trip-иться; без cert / чужий cert (від іншого CA) → відмова
/// конекту (server-side rejection). ГЛИБОКІ mTLS-DoD (G5/G10/повний admin-флоу) — секція 4.
/// </summary>
public sealed class SecureAdminPortSmokeTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-mtls-smoke-" + Guid.NewGuid().ToString("N"));

    private readonly string _secretsDir;
    private readonly DurableStore _store;
    private readonly ServiceProvider _provider;
    private readonly SecureAdminRpcServer _server;
    private readonly int _port;
    private readonly List<IDisposable> _clientCleanup = [];

    public SecureAdminPortSmokeTests()
    {
        Directory.CreateDirectory(_dataDir);

        // Реальний провіжн секретів на томі (CA + server/admin cert + пеппери), як у продакшн-старті.
        var secrets = new SecretMaterialProvisioner(NullLogger<SecretMaterialProvisioner>.Instance)
            .Provision(_dataDir, hostname: null);
        _secretsDir = Path.Combine(_dataDir, "secrets");

        // Стор із замапленим SPKI admin-cert-а (bootstrap-шлях відтворений напряму).
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
        _store.UpsertIdentity(new IdentityRecord("admin", "admin", [], DateTimeOffset.UtcNow));
        var adminSpki = NodeIdentity.ComputeSpkiThumbprint(
            X509Certificate2.CreateFromPem(File.ReadAllText(secrets.AdminClientCertificatePath)));
        _store.UpsertAdminCredential(
            new AdminCredentialRecord(adminSpki, "admin", Revoked: false, DateTimeOffset.UtcNow));

        // Мінімальний DI-контейнер для сесії: тільки ping-таргет (Public-політика), без signal-cli.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new JsonRpcServerConfig { Host = "127.0.0.1", Port = 0 });
        services.AddSingleton<IRpcPolicyRegistry, SignalRpcPolicyRegistry>();
        services.AddSingleton<IDurableStore>(_store);
        services.AddSingleton<IRpcServiceRegistry, PingOnlyRegistry>();
        services.AddSingleton<IEventProcessor, NoopEventProcessor>();
        _provider = services.BuildServiceProvider();

        var transport = BuildTransport();
        _port = FreeLoopbackPort();
        _server = ActivatorUtilities.CreateInstance<SecureAdminRpcServer>(
            _provider, transport, IPAddress.Loopback, _port);
        Assert.True(_server.Start(), "mTLS admin-сервер не стартував.");
    }

    [Fact]
    public async Task ValidAdminCert_Connects_AndPingRoundTrips()
    {
        using var clientCert = LoadClientCertificate(
            Path.Combine(_secretsDir, "admin-client.crt"), Path.Combine(_secretsDir, "admin-client.key"));

        using var ws = await ConnectAsync(clientCert);
        Assert.Equal(WebSocketState.Open, ws.State);

        var response = await PingAsync(ws);
        Assert.Contains("pong", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoClientCert_ConnectionRefused()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(clientCertificate: null));
    }

    [Fact]
    public async Task RogueCertFromDifferentCa_ConnectionRefused()
    {
        // Чужий CA + client-cert від нього → наш CustomRootTrustValidator не будує ланцюг → server-side відмова.
        var now = DateTimeOffset.UtcNow;
        using var rogueCa = CertificateFactory.CreateCertificateAuthority(now.AddMinutes(-5), now.AddYears(1));
        using var rogueLeaf = CertificateFactory.IssueAdminClientCertificate(rogueCa, now.AddMinutes(-5), now.AddDays(30));
        using var rogue = X509CertificateLoader.LoadPkcs12(rogueLeaf.Export(X509ContentType.Pkcs12), password: null);

        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(rogue));
    }

    // ---------- harness ----------

    private SecureTransport BuildTransport()
    {
        using var pem = X509Certificate2.CreateFromPemFile(
            Path.Combine(_secretsDir, "server.crt"), Path.Combine(_secretsDir, "server.key"));
        var serverCert = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null);
        var ca = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(_secretsDir, "ca.crt")));

        var tls = new TlsServerOptions
        {
            ServerCertificate = serverCert,
            ClientCertificateRequired = true,
            TrustedRoots = [ca],
            // Наш авто-CA без CRL/OCSP — NoCheck (як продакшн BuildTransport); відкликання — durable-важіль.
            RevocationMode = X509RevocationMode.NoCheck,
            SslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
        };
        var validator = new CustomRootTrustValidator(
            tls.TrustedRoots, spkiPins: null, tls.RevocationMode, NullLogger<CustomRootTrustValidator>.Instance);
        return SecureTransport.Create(tls, validator, new SpiffeNodeIdentityResolver(), NullLogger<SecureTransport>.Instance);
    }

    private async Task<ClientWebSocket> ConnectAsync(X509Certificate2? clientCertificate)
    {
        // Явний invoker: приватний CA деплою не в системному trust, тож клієнт довіряє server-cert-у через
        // CustomRootTrust-ланцюг до нашого CA (на рівні валідації ланцюга). client-cert (mTLS) — у SslOptions
        // інвокера. SAN server-cert-а містить 127.0.0.1, тож ім'я збігається.
        var caCert = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(_secretsDir, "ca.crt")));
        _clientCleanup.Add(caCert);

        var chainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        chainPolicy.CustomTrustStore.Add(caCert);

        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = chainPolicy },
        };
        if (clientCertificate is not null)
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };

        var invoker = new HttpMessageInvoker(handler);
        _clientCleanup.Add(invoker);

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

    private static async Task<string> PingAsync(ClientWebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var request = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
        await ws.SendAsync(request, WebSocketMessageType.Binary, endOfMessage: true, cts.Token);

        var buffer = new byte[8192];
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static X509Certificate2 LoadClientCertificate(string certPath, string keyPath)
    {
        using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null);
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
        foreach (var d in _clientCleanup)
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

    // ---------- fakes ----------

    private sealed class PingOnlyRegistry : IRpcServiceRegistry
    {
        public void RegisterServices(JsonRpc jsonRpc, Guid clientId) =>
            jsonRpc.AddLocalRpcTarget(
                new PingTarget(),
                new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.CamelCase });

        private sealed class PingTarget
        {
            public Task<string> Ping() => Task.FromResult("pong");
        }
    }

    private sealed class NoopEventProcessor : IEventProcessor
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void RegisterClient(Guid clientId, Func<string, object[], Task> notificationHandler) { }
        public void UnregisterClient(Guid clientId) { }
    }
}

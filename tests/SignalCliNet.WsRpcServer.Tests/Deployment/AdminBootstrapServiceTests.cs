using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Deployment;

/// <summary>
/// Пінить admin bootstrap (add-invites-admin, task 2.2): перший старт створює одну admin-identity й
/// прив'язує SPKI admin-cert-а; двічі → рівно одна admin-identity (ідемпотентність); наявну admin-identity
/// переюзаємо замість створення нової.
/// </summary>
public sealed class AdminBootstrapServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-adminboot-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _adminCertPath;
    private readonly string _expectedSpki;
    private readonly DurableStore _store;

    public AdminBootstrapServiceTests()
    {
        Directory.CreateDirectory(_dataDir);

        // Реальний admin client-cert від тестового CA (той самий шлях, що провіжнер на томі).
        using var ca = CertificateFactory.CreateCertificateAuthority(Now.AddMinutes(-5), Now.AddYears(1));
        using var adminCert = CertificateFactory.IssueAdminClientCertificate(ca, Now.AddMinutes(-5), Now.AddDays(30));
        _expectedSpki = NodeIdentity.ComputeSpkiThumbprint(adminCert);

        _adminCertPath = Path.Combine(_dataDir, "admin-client.crt");
        File.WriteAllText(_adminCertPath, adminCert.ExportCertificatePem());

        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
    }

    private AdminBootstrapService NewService() =>
        new(_store, NullLogger<AdminBootstrapService>.Instance);

    [Fact]
    public void Bootstrap_FirstRun_CreatesAdminIdentity_AndBindsSpki()
    {
        NewService().Bootstrap(_adminCertPath);

        var admin = Assert.Single(_store.GetIdentities(), i => i.Role == "admin");
        Assert.Equal(AdminBootstrapService.DefaultAdminIdentityId, admin.Id);

        var credential = _store.GetAdminCredential(_expectedSpki);
        Assert.NotNull(credential);
        Assert.Equal(AdminBootstrapService.DefaultAdminIdentityId, credential!.IdentityId);
        Assert.False(credential.Revoked);
    }

    [Fact]
    public void Bootstrap_Twice_IsIdempotent_SingleAdminIdentity()
    {
        var service = NewService();
        service.Bootstrap(_adminCertPath);
        service.Bootstrap(_adminCertPath);

        Assert.Single(_store.GetIdentities(), i => i.Role == "admin");
        Assert.NotNull(_store.GetAdminCredential(_expectedSpki));
    }

    [Fact]
    public void Bootstrap_WhenAdminAlreadyExists_ReusesIt()
    {
        _store.UpsertIdentity(new IdentityRecord("pre-existing-admin", "admin", [], Now));

        NewService().Bootstrap(_adminCertPath);

        // Не плодимо другого адміна; SPKI прив'язаний до наявної admin-identity.
        Assert.Single(_store.GetIdentities(), i => i.Role == "admin");
        Assert.Equal("pre-existing-admin", _store.GetAdminCredential(_expectedSpki)!.IdentityId);
    }

    [Fact]
    public void Bootstrap_MissingCert_IsNoOp()
    {
        NewService().Bootstrap(Path.Combine(_dataDir, "does-not-exist.crt"));

        Assert.DoesNotContain(_store.GetIdentities(), i => i.Role == "admin");
    }

    public void Dispose()
    {
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
}

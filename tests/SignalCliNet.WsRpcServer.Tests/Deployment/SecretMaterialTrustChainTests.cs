using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Deployment;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Deployment;

/// <summary>
/// DoD-пін (5.2-суміжний, адверсарний): цілісність ланцюга довіри після часткового пошкодження
/// секретів на томі. Інваріант — після БУДЬ-ЯКОГО прогону <c>Provision</c> leaf-сертифікати на
/// диску МУСЯТЬ бути підписані поточним <c>ca.crt</c>; «нове CA + старі leaf-и» — заборонений
/// стан (мовчки зламаний trust: nginx віддає старий server.crt, клієнти довіряють новому ca.crt).
/// </summary>
/// <remarks>
/// Сценарій відновлення з неповного бекапа / ручного видалення: зник лише <c>ca.key</c> —
/// persist-once перегенеровує пару CA, і тоді валідні за терміном старі leaf-и зобов'язані бути
/// переоформлені новим CA у тому ж прогоні.
/// </remarks>
public sealed class SecretMaterialTrustChainTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-trustchain-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // Темп-каталог приберmay ОС; не валимо тест на cleanup.
        }
    }

    private static SecretMaterialProvisioner NewProvisioner() =>
        new(NullLogger<SecretMaterialProvisioner>.Instance);

    [Fact]
    public void Provision_AfterCaKeyLoss_LeafCertsChainToCurrentCa()
    {
        var first = NewProvisioner().Provision(_dataDir, hostname: null);

        // Частковий tamper: приватний ключ CA зник, сертифікат лишився (неповний restore).
        File.Delete(first.CaKeyPath);

        var second = NewProvisioner().Provision(_dataDir, hostname: null);

        using var ca = X509Certificate2.CreateFromPemFile(
            second.CaCertificatePath, second.CaKeyPath);
        AssertChainsTo(ca, second.ServerCertificatePath, "server");
        AssertChainsTo(ca, second.AdminClientCertificatePath, "admin");
    }

    [Fact]
    public void Provision_AfterLeafKeyLoss_LeafIsReissued()
    {
        var first = NewProvisioner().Provision(_dataDir, hostname: null);

        // Зник лише приватний ключ server-cert — leaf мусить бути переоформлений, не впасти.
        File.Delete(first.ServerKeyPath);

        var second = NewProvisioner().Provision(_dataDir, hostname: null);

        Assert.True(File.Exists(second.ServerKeyPath));
        using var ca = X509Certificate2.CreateFromPemFile(
            second.CaCertificatePath, second.CaKeyPath);
        AssertChainsTo(ca, second.ServerCertificatePath, "server");
    }

    private static void AssertChainsTo(X509Certificate2 ca, string leafCertPath, string name)
    {
        using var leaf = X509Certificate2.CreateFromPem(File.ReadAllText(leafCertPath));

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        Assert.True(
            chain.Build(leaf),
            $"Leaf '{name}' не будує ланцюг до ПОТОЧНОГО ca.crt — на томі «нове CA + старий leaf» " +
            "(заборонений стан: мовчки зламаний client-trust).");
    }
}

using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить <see cref="AbusePepperProvider"/>: лінива читка + base64-декод файла <c>pepper_abuse</c> з тому
/// (той самий формат, що пише SecretMaterialProvisioner) і кеш при повторних викликах.
/// </summary>
public class AbusePepperProviderTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-abuse-pepper-tests-" + Guid.NewGuid().ToString("N"));

    public AbusePepperProviderTests() => Directory.CreateDirectory(Path.Combine(_dataDir, "secrets"));

    private AbusePepperProvider NewProvider() =>
        new(Options.Create(new PersistenceOptions { DataDirectory = _dataDir }));

    [Fact]
    public void GetPepper_DecodesBase64File()
    {
        var expected = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(Path.Combine(_dataDir, "secrets", "pepper_abuse"), Convert.ToBase64String(expected));

        var provider = NewProvider();

        Assert.Equal(expected, provider.GetPepper());
        // Другий виклик віддає той самий (кешований) масив.
        Assert.Same(provider.GetPepper(), provider.GetPepper());
    }

    [Fact]
    public void GetPepper_TrimsTrailingNewline()
    {
        var expected = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(
            Path.Combine(_dataDir, "secrets", "pepper_abuse"), Convert.ToBase64String(expected) + "\n");

        Assert.Equal(expected, NewProvider().GetPepper());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
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

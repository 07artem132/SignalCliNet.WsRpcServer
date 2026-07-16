using System.Security.Cryptography;
using System.Text;
using SignalCliNet.WsRpcServer.Security;

namespace SignalCliNet.WsRpcServer.Tests.Security.Admission;

/// <summary>
/// Тестова реалізація <see cref="IAbusePepperProvider"/> з фіксованим ключем (без читання файла на томі).
/// Дає й статичний хелпер очікуваного хешу, щоб тести могли пінити «hash != сирий номер».
/// </summary>
internal sealed class FakeAbusePepperProvider(byte[]? key = null) : IAbusePepperProvider
{
    private readonly byte[] _key = key ?? Encoding.UTF8.GetBytes("test-abuse-pepper-key-0123456789");

    public byte[] GetPepper() => _key;

    /// <summary>Обчислює той самий хеш, що й трекер: base64(HMAC-SHA256(recipient, pepper)).</summary>
    public string Hash(string recipient) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(recipient)));
}

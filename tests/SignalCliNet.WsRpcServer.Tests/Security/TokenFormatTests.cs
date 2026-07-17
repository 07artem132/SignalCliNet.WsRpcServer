using System.Security.Cryptography;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить формат opaque-токена (task 1.1): round-trip префікс/версія/payload, fast-reject по checksum,
/// стабільність HMAC для того самого payload, канон pre-image = ЛИШЕ decoded random (W18).
/// </summary>
public class TokenFormatTests
{
    [Fact]
    public void Create_ThenTryParse_RoundTrips()
    {
        // Тест-пін round-trip (1.1): версія й payload відновлюються один-в-один.
        var token = TokenFormat.Create(3, out var payload);

        Assert.StartsWith("ptzh_3_", token);
        Assert.True(TokenFormat.TryParse(token, out var version, out var parsedPayload));
        Assert.Equal(3, version);
        Assert.Equal(payload, parsedPayload);
        Assert.Equal(32, payload.Length); // 256-bit ентропія
    }

    [Fact]
    public void Create_ProducesFixedSegmentLengths()
    {
        var token = TokenFormat.Create(1, out _);

        // ptzh _ <ver> _ <payload=43> _ <checksum=6>. Payload/checksum самі можуть містити '_',
        // тож перевіряємо структуру за фіксованими довжинами хвоста, не split-ом.
        Assert.StartsWith("ptzh_1_", token);
        var tail = token["ptzh_1_".Length..];
        Assert.Equal(43 + 1 + 6, tail.Length);
        Assert.Equal('_', tail[43]);
    }

    [Fact]
    public void Create_ProducesDistinctTokens()
    {
        var a = TokenFormat.Create(1, out _);
        var b = TokenFormat.Create(1, out _);
        Assert.NotEqual(a, b); // CSPRNG payload
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("ptzh_1_tooshort_xxxxxx")]
    [InlineData("ptzh_0_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA_AAAAAA")] // версія < 1
    [InlineData("ptzh__AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA_AAAAAA")] // порожня версія
    [InlineData("wrong_1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA_AAAAAA")] // не той префікс
    public void TryParse_RejectsMalformed(string token)
    {
        Assert.False(TokenFormat.TryParse(token, out _, out _));
    }

    [Fact]
    public void TryParse_RejectsCorruptedPayload_ChecksumMismatch()
    {
        var token = TokenFormat.Create(1, out _);

        // Псуємо один символ у payload-сегменті: checksum більше не збігається → fast-reject.
        var chars = token.ToCharArray();
        var payloadIndex = "ptzh_1_".Length;
        chars[payloadIndex] = chars[payloadIndex] == 'A' ? 'B' : 'A';
        var corrupted = new string(chars);

        Assert.False(TokenFormat.TryParse(corrupted, out _, out _));
    }

    [Fact]
    public void ComputeHash_IsStable_ForSamePayloadAndPepper()
    {
        TokenFormat.Create(1, out var payload);
        var pepper = RandomNumberGenerator.GetBytes(32);

        Assert.Equal(TokenFormat.ComputeHash(payload, pepper), TokenFormat.ComputeHash(payload, pepper));
    }

    [Fact]
    public void ComputeHash_Differs_ForDifferentPepper()
    {
        TokenFormat.Create(1, out var payload);

        var hashA = TokenFormat.ComputeHash(payload, RandomNumberGenerator.GetBytes(32));
        var hashB = TokenFormat.ComputeHash(payload, RandomNumberGenerator.GetBytes(32));
        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void ComputeHash_PreImage_IsDecodedPayloadOnly()
    {
        // W18-канон: pre-image = ЛИШЕ decoded random payload (не префікс/версія/checksum).
        // Незалежне HMAC(pepper, payload) має збігтись із ComputeHash.
        TokenFormat.Create(7, out var payload);
        var pepper = RandomNumberGenerator.GetBytes(32);

        var expected = Convert.ToBase64String(HMACSHA256.HashData(pepper, payload));
        Assert.Equal(expected, TokenFormat.ComputeHash(payload, pepper));
    }
}

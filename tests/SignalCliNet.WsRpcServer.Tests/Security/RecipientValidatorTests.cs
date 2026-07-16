using SignalCliNet.WsRpcServer.Security;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

// Юніт-тести чистого RecipientValidator (не DoD-диспетч-тест 4.5).
public class RecipientValidatorTests
{
    [Theory]
    [InlineData("+10000000002")]
    [InlineData("+380501234567")]
    [InlineData("2c5e1f9a-1234-4abc-8def-0123456789ab")] // UUID
    [InlineData("alice.42")]                              // username
    public void TryNormalize_ValidRecipient_ReturnsTrue(string raw)
    {
        Assert.True(RecipientValidator.TryNormalizeUserRecipient(raw, out var normalized, out var kind));
        Assert.NotEqual(RecipientValidator.RecipientKind.Invalid, kind);
        Assert.False(string.IsNullOrEmpty(normalized));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+")]           // '+' без цифр
    [InlineData("+0123456789")] // код країни '0'
    [InlineData("12345")]       // схоже на номер без '+'
    [InlineData("not a number")]// пробіл
    [InlineData("++15551234")]  // подвійний '+'
    public void TryNormalize_MalformedRecipient_ReturnsFalse(string raw)
    {
        Assert.False(RecipientValidator.TryNormalizeUserRecipient(raw, out _, out var kind));
        Assert.Equal(RecipientValidator.RecipientKind.Invalid, kind);
    }

    [Fact]
    public void AssertValid_MalformedInList_ThrowsInvalidParams()
    {
        var ex = Assert.Throws<RpcErrorException>(
            () => RecipientValidator.AssertValidUserRecipients(["+10000000002", "12345"]));
        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
    }

    [Fact]
    public void AssertValid_SkipsEmptyEntries_LikeAdapter()
    {
        // Порожні відсіює адаптер — валідатор не валить через них весь виклик.
        RecipientValidator.AssertValidUserRecipients(["", "  ", "+10000000002"]);
    }

    [Fact]
    public void AssertValid_AllWellFormed_DoesNotThrow()
    {
        RecipientValidator.AssertValidUserRecipients(["+10000000002", "alice.42"]);
    }
}

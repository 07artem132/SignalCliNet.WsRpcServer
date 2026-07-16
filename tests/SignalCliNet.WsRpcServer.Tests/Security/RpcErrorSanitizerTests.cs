using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Security;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

// Юніт-тести санітизації клієнтських помилок (3.1).
public class RpcErrorSanitizerTests
{
    [Fact]
    public void RpcErrorException_HonorsCodeMessageAndData()
    {
        var ex = new RpcErrorException(JsonRpcErrorCode.InvalidParams, "Malformed recipient.", new { field = "x" });

        var detail = RpcErrorSanitizer.CreateDetail(ex, "sendTextMessage", NullLogger.Instance);

        Assert.Equal(JsonRpcErrorCode.InvalidParams, detail.Code);
        Assert.Equal("Malformed recipient.", detail.Message);
        Assert.NotNull(detail.Data); // author-controlled ErrorData зберігається
    }

    [Fact]
    public void UnexpectedException_ReturnsGenericInternalError_WithoutDetails()
    {
        var ex = new InvalidOperationException("signal-cli exploded at /secret/path with token abc123");

        var detail = RpcErrorSanitizer.CreateDetail(ex, "listAccounts", NullLogger.Instance);

        Assert.Equal(RpcErrorSanitizer.InternalErrorCode, (int)detail.Code);
        Assert.Equal("Internal server error.", detail.Message);
        Assert.Null(detail.Data); // жодних внутрішніх деталей клієнту
        // Санітизоване повідомлення не містить внутрішніх деталей винятку.
        Assert.DoesNotContain("secret", detail.Message);
        Assert.DoesNotContain("abc123", detail.Message);
    }
}

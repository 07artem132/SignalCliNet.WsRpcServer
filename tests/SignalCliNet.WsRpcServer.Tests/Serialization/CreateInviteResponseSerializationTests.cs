using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SignalCliNet.WsRpcServer.Model;
using SignalCliNet.WsRpcServer.Serialization;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Serialization;

/// <summary>
/// Пінить, що <see cref="CreateInviteResponse"/> (несе плейнтекст-код інвайту — секрет, тож СВІДОМО не
/// зареєстрований у source-gen контексті, A1) серіалізується reflection-fallback'ом форматтера, а
/// non-secret <see cref="RevokeIdentityResponse"/> — навпаки, у source-gen контексті.
/// </summary>
public class CreateInviteResponseSerializationTests
{
    private static readonly JsonSerializerOptions SessionLikeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            SignalCliSerializerContext.Default, new DefaultJsonTypeInfoResolver()),
    };

    [Fact]
    public void CreateInviteResponse_SerializesCodeViaReflectionFallback()
    {
        var expiresAt = new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero);
        var json = JsonSerializer.Serialize(new CreateInviteResponse("K7QT-9F2M", expiresAt), SessionLikeOptions);

        Assert.Contains("\"code\":\"K7QT-9F2M\"", json);
        Assert.Contains("\"expiresAt\":", json);
    }

    [Fact]
    public void CreateInviteResponse_IsNotRegisteredInSourceGenContext()
    {
        // Свідомо НЕ у source-gen контексті (A1: несе секрет-код) — лише reflection-fallback.
        Assert.Null(SignalCliSerializerContext.Default.GetTypeInfo(typeof(CreateInviteResponse)));
    }

    [Fact]
    public void RevokeIdentityResponse_IsRegisteredInSourceGenContext()
    {
        // Non-secret → у source-gen контексті (fast-path), і serialization guard його не блокує.
        Assert.NotNull(SignalCliSerializerContext.Default.GetTypeInfo(typeof(RevokeIdentityResponse)));
    }
}

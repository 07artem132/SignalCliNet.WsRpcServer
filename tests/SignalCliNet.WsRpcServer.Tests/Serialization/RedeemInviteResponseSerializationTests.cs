using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SignalCliNet.WsRpcServer.Model;
using SignalCliNet.WsRpcServer.Serialization;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Serialization;

/// <summary>
/// Пінить, що <see cref="RedeemInviteResponse"/> (несе плейнтекст-токен, тож СВІДОМО не зареєстрований у
/// source-gen контексті — інакше впав би A1-guard) коректно серіалізується reflection-fallback'ом
/// форматтера сесії: клієнт отримує <c>identityId</c>+<c>token</c> у camelCase. Це фіксує рантайм-шлях
/// відповіді redeemInvite (той самий принцип, що плейнтекст-токени pop.prove ack повз source-gen).
/// </summary>
public class RedeemInviteResponseSerializationTests
{
    // Дзеркалить конфіг форматтера сесії (SignalRpcSession.CreateJsonFormatter): source-gen контекст +
    // reflection-fallback, camelCase, ignore-null.
    private static readonly JsonSerializerOptions SessionLikeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(
            SignalCliSerializerContext.Default, new DefaultJsonTypeInfoResolver()),
    };

    [Fact]
    public void RedeemInviteResponse_SerializesTokenViaReflectionFallback()
    {
        var json = JsonSerializer.Serialize(new RedeemInviteResponse("id-abc", "tok-xyz"), SessionLikeOptions);

        Assert.Contains("\"identityId\":\"id-abc\"", json);
        Assert.Contains("\"token\":\"tok-xyz\"", json);
    }

    [Fact]
    public void RedeemInviteResponse_IsNotRegisteredInSourceGenContext()
    {
        // Свідомо НЕ у source-gen контексті (A1: несе токен) — резолвиться лише reflection-fallback'ом.
        Assert.Null(SignalCliSerializerContext.Default.GetTypeInfo(typeof(RedeemInviteResponse)));
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using StreamJsonRpc;

namespace SignalCliNet.WsRpcServer.Serialization;

/// <summary>
/// Builds the JSON-RPC message formatter shared by the Signal RPC transports: camelCase naming, null
/// omission, and the source-gen <see cref="SignalCliSerializerContext"/> combined with a reflection
/// fallback (for types the source generator cannot reference — custom converters, secret-carrying DTOs).
/// </summary>
/// <remarks>
/// Винесено сюди, щоб плейн-сесія (<c>SignalRpcSession</c>) і mTLS admin-сесія (<c>SecureAdminRpcSession</c>)
/// будували ІДЕНТИЧНИЙ форматтер: секрет-несучі DTO (<c>RedeemInviteResponse</c>, <c>CreateInviteResponse</c>)
/// свідомо не в source-gen контексті (A1) і резолвяться reflection-fallback'ом — обидва транспорти мусять
/// це вміти однаково. Плейн/секюр ієрархії роздільні (framework rule #11), але серіалізатор — спільний.
/// </remarks>
public static class SignalRpcFormatter
{
    /// <summary>Creates a configured <see cref="SystemTextJsonFormatter"/> (camelCase + source-gen + reflection fallback).</summary>
    public static SystemTextJsonFormatter Create()
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        formatter.JsonSerializerOptions.TypeInfoResolver = JsonTypeInfoResolver.Combine(
            SignalCliSerializerContext.Default,
            new DefaultJsonTypeInfoResolver());
        return formatter;
    }
}

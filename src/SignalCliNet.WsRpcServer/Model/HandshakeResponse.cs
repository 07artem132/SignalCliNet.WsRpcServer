using System.Text.Json.Serialization;

namespace SignalCliNet.WsRpcServer.Model;

/// <summary>
/// The server's reply to a handshake: the contract version it speaks, the oldest client version it
/// accepts, and the capabilities the client may rely on (A5).
/// </summary>
/// <param name="ApiVersion">The contract version this server speaks.</param>
/// <param name="MinimumApiVersion">The oldest client contract version this server accepts.</param>
/// <param name="Capabilities">Named protocol capabilities the client may rely on.</param>
public sealed record HandshakeResponse(
    [property: JsonPropertyName("apiVersion")] int ApiVersion,
    [property: JsonPropertyName("minimumApiVersion")] int MinimumApiVersion,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities);

/// <summary>
/// The typed <c>error.data</c> payload attached to an "upgrade required" handshake failure, so the
/// client learns exactly which contract version the server needs rather than getting a silent −32601.
/// </summary>
/// <param name="ServerApiVersion">The contract version the server speaks.</param>
/// <param name="MinimumApiVersion">The oldest client contract version the server accepts.</param>
public sealed record ApiVersionRequirement(
    [property: JsonPropertyName("serverApiVersion")] int ServerApiVersion,
    [property: JsonPropertyName("minimumApiVersion")] int MinimumApiVersion);

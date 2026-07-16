namespace SignalCliNet.WsRpcServer.Model;

/// <summary>
/// The server's JSON-RPC contract version and the named capabilities a client can negotiate against
/// during the handshake (A5). A single source of truth so the handshake and any version checks agree.
/// </summary>
public static class SignalRpcApiVersion
{
    /// <summary>The contract version this server speaks.</summary>
    public const int Current = 1;

    /// <summary>The oldest client contract version this server still accepts.</summary>
    public const int MinimumSupported = 1;

    /// <summary>
    /// JSON-RPC error code for an incompatible contract version (implementation-defined server-error
    /// range −32000..−32099). Signals the client that an upgrade is required, rather than a silent −32601.
    /// </summary>
    public const int UpgradeRequiredErrorCode = -32010;

    /// <summary>Named protocol capabilities the client may rely on when talking to this server.</summary>
    public static IReadOnlyList<string> Capabilities { get; } =
    [
        "core",              // базовий RPC-контракт (send/link/list/subscribe)
        "authz-chokepoint",  // pre-dispatch default-deny + anti-IDOR + outbound-фільтр
    ];

    /// <summary>Whether <paramref name="clientApiVersion"/> is within the supported range.</summary>
    /// <param name="clientApiVersion">The contract version the client claims.</param>
    /// <returns><c>true</c> if the server can serve the client without an upgrade on either side.</returns>
    public static bool IsCompatible(int clientApiVersion) =>
        clientApiVersion is >= MinimumSupported and <= Current;
}

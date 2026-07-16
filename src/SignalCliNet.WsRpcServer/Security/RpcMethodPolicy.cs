namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// The declarative authorization descriptor for a single RPC method: its policy kind plus the
/// metadata the chokepoint needs to enforce it (where the account/recipients arguments live, and
/// which outbound filter to apply).
/// </summary>
/// <remarks>
/// Argument positions carry BOTH a name and an index so the chokepoint can read an argument from a
/// request whether the client sent named (JSON object) or positional (JSON array) parameters — via
/// <c>JsonRpcRequest.TryGetArgumentByNameOrIndex</c>. Wire method names are camelCase (the registry
/// registers targets with <c>CommonMethodNameTransforms.CamelCase</c>).
/// </remarks>
public sealed record RpcMethodPolicy
{
    /// <summary>The camelCase wire method name this policy applies to.</summary>
    public required string Method { get; init; }

    /// <summary>The authorization policy for the method.</summary>
    public required RpcPolicyKind Kind { get; init; }

    /// <summary>Name of the <c>account</c> argument to guard (anti-IDOR), or <c>null</c> if the method carries none.</summary>
    public string? AccountArgName { get; init; }

    /// <summary>Zero-based position of the <c>account</c> argument (for positional calls).</summary>
    public int AccountArgIndex { get; init; } = -1;

    /// <summary>Name of the <c>recipients</c> argument to validate (D11), or <c>null</c> if the method carries none.</summary>
    public string? RecipientsArgName { get; init; }

    /// <summary>Zero-based position of the <c>recipients</c> argument (for positional calls).</summary>
    public int RecipientsArgIndex { get; init; } = -1;

    /// <summary>Which fail-closed outbound filter to apply to the result (read output visibility).</summary>
    public ReadOutputKind Output { get; init; } = ReadOutputKind.None;
}

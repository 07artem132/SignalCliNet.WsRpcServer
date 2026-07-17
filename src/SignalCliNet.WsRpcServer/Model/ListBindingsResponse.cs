namespace SignalCliNet.WsRpcServer.Model;

/// <summary>
/// One of the caller's own group-bindings, as surfaced by <c>listMyBindings</c> (self-management, task 2.6).
/// Carries only the caller's own, non-secret binding metadata — never another identity's data.
/// </summary>
/// <param name="BindingId">The binding id (opaque; the handle <c>revokeMyBinding</c> takes).</param>
/// <param name="GroupId">The group the binding authorizes sends to.</param>
/// <param name="ExpiresAt">The binding's absolute backstop expiry.</param>
public sealed record GroupBindingInfo(string BindingId, string GroupId, DateTimeOffset ExpiresAt);

/// <summary>
/// RPC result of <c>listMyBindings</c> (task 2.6): the caller's own active group-bindings. Non-secret —
/// registered in the source-gen serializer context.
/// </summary>
/// <param name="Bindings">The caller's own bindings (only their own — R3.2/no other identity's data).</param>
public sealed record ListBindingsResponse(IReadOnlyList<GroupBindingInfo> Bindings);

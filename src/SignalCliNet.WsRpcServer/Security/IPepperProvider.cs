namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Supplies the versioned pepper (HMAC key) used to hash access tokens at rest. A version-hint in the
/// token prefix selects the pepper, so a pepper rotation stays a single-lookup operation (D10).
/// </summary>
/// <remarks>
/// Значення пеппера — секрет: реалізації НІКОЛИ не логують його і не віддають у stdout/env.
/// </remarks>
public interface IPepperProvider
{
    /// <summary>The current pepper version new tokens are issued under.</summary>
    int CurrentVersion => 1;

    /// <summary>
    /// Returns the pepper bytes for <paramref name="version"/>.
    /// </summary>
    /// <param name="version">The pepper version (from a token's version-hint).</param>
    /// <returns>The pepper key bytes.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="version"/> is unknown.</exception>
    byte[] GetPepper(int version);
}

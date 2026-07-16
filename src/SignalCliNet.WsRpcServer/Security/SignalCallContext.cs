namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Ambient, per-invocation access to the current <see cref="SignalPrincipal"/> (V8).
/// </summary>
/// <remarks>
/// V8: адаптери root-resolved/shared (singleton-ish через DI), тож вони НЕ можуть тримати
/// per-user стан — інакше два конекти різних ідентичностей побачили б стан одне одного через
/// спільний адаптер. Principal читається per-invocation з цього call-context, який chokepoint
/// (<see cref="AuthorizingSignalJsonRpc"/>) виставляє на час диспетчу кожного виклику.
///
/// Реалізовано через <see cref="AsyncLocal{T}"/>: значення потрапляє в асинхронний потік конкретного
/// виклику й не «протікає» між викликами. У Фазі 1 chokepoint сам виконує всі перевірки (адаптери
/// principal не читають), але seam існує наперед — коли адаптеру знадобиться principal, він бере його
/// звідси, а не з конструктора (V8).
/// </remarks>
public static class SignalCallContext
{
    private static readonly AsyncLocal<SignalPrincipal?> CurrentPrincipal = new();

    /// <summary>
    /// The principal for the RPC invocation in flight on the current async context, or <c>null</c>
    /// outside a dispatch.
    /// </summary>
    public static SignalPrincipal? Principal => CurrentPrincipal.Value;

    /// <summary>
    /// Sets <paramref name="principal"/> as the ambient principal for the duration of the returned
    /// scope; disposing the scope restores the previous value. Intended for the chokepoint to wrap a
    /// single dispatch.
    /// </summary>
    /// <param name="principal">The principal to make ambient.</param>
    /// <returns>A scope that restores the previous ambient principal on dispose.</returns>
    internal static Scope Enter(SignalPrincipal? principal)
    {
        var previous = CurrentPrincipal.Value;
        CurrentPrincipal.Value = principal;
        return new Scope(previous);
    }

    /// <summary>
    /// Restores the previously-ambient principal on dispose. A <c>readonly struct</c> so entering the
    /// context allocates nothing on the heap.
    /// </summary>
    internal readonly struct Scope(SignalPrincipal? previous) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => CurrentPrincipal.Value = previous;
    }
}

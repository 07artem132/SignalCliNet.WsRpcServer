using System.Reflection;
using StreamJsonRpc;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Belt-and-suspenders startup guard (W23): asserts that every discoverable RPC method has an explicit
/// policy in the registry. Fails loudly at startup so an unclassified method can never ship silently.
/// </summary>
/// <remarks>
/// Dispatch-time default-deny (<see cref="AuthorizingSignalJsonRpc"/>) лишається ПЕРВИННОЮ гарантією —
/// навіть якби цей guard не запустився, неполітизований метод усе одно отримав би <c>-32601</c>. Guard
/// перетворює цю тиху відмову на гучний фейл СТАРТУ, щоб дефект «забув класифікувати метод» ловився в
/// CI/на старті, а не в проді. Перелічуємо ті самі RPC-інтерфейси, що їх піднімає авто-дискавері
/// (типи цієї збірки, похідні від <see cref="IRpcService"/>), і зіставляємо camelCase-імена методів із
/// реєстром — рівно так, як їх бачить диспетч.
/// </remarks>
public static class RpcPolicyCoverageValidator
{
    private static readonly Func<string, string> CamelCase = CommonMethodNameTransforms.CamelCase;

    /// <summary>
    /// Validates that every RPC method declared on this assembly's <see cref="IRpcService"/> interfaces
    /// has a policy in <paramref name="registry"/>. Throws if any is missing.
    /// </summary>
    /// <param name="registry">The declarative policy registry.</param>
    /// <exception cref="InvalidOperationException">If one or more RPC methods have no explicit policy.</exception>
    public static void ValidateCoverage(IRpcPolicyRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var uncovered = FindUncoveredMethods(registry, typeof(RpcPolicyCoverageValidator).Assembly);
        if (uncovered.Count == 0)
            return;

        throw new InvalidOperationException(
            "Chokepoint: наступні RPC-методи не мають явної політики авторизації (default-deny валить їх у " +
            "рантаймі; класифікуй їх у SignalRpcPolicyRegistry): " + string.Join(", ", uncovered));
    }

    /// <summary>
    /// Returns the camelCase wire names of RPC methods (on <paramref name="assembly"/>'s
    /// <see cref="IRpcService"/> interfaces) that have no policy in <paramref name="registry"/>.
    /// </summary>
    /// <param name="registry">The policy registry.</param>
    /// <param name="assembly">The assembly whose RPC interfaces to enumerate.</param>
    /// <returns>Sorted, de-duplicated list of uncovered method names.</returns>
    public static IReadOnlyList<string> FindUncoveredMethods(IRpcPolicyRegistry registry, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(assembly);

        var uncovered = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var iface in assembly.GetTypes().Where(IsRpcServiceInterface))
        {
            foreach (var method in iface.GetMethods())
            {
                var wireName = CamelCase(method.Name);
                if (registry.Resolve(wireName) is null)
                    uncovered.Add(wireName);
            }
        }

        return [.. uncovered];
    }

    // RPC-інтерфейс = інтерфейс, похідний від IRpcService, але не самі маркери IRpcService/IClientAwareRpcService.
    private static bool IsRpcServiceInterface(Type type) =>
        type.IsInterface &&
        typeof(IRpcService).IsAssignableFrom(type) &&
        type != typeof(IRpcService) &&
        type != typeof(IClientAwareRpcService);
}

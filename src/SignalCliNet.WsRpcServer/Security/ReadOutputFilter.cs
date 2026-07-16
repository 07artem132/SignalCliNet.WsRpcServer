using Microsoft.Extensions.Logging;
using SignalCli.Models.Signal.Accounts;
using SignalCli.Models.Signal.Groups;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// The fail-closed outbound filter for <see cref="RpcPolicyKind.AccountQuery"/> results: narrows a read
/// response to exactly what the principal is allowed to see, before it leaves the server.
/// </summary>
/// <remarks>
/// W22 fail-closed: будь-який виняток під час фільтрації → ПОРОЖНІЙ типізований результат, ніколи
/// нефільтровані («raw») дані. Видимість (розділ 2.3):
/// <list type="bullet">
///   <item>R3.5 — own-linked акаунти приватні власнику (user бачить лише свої);</item>
///   <item>R3.6 — admin бачить УСІ акаунти (read-only нагляд; visibility ≠ operation);</item>
///   <item>R3.2 — <c>listGroups</c> віддає ВИКЛЮЧНО claim'нуті/власні групи caller'а (закриває G9);
///         admin спец-видимості груп не має.</item>
/// </list>
/// </remarks>
public static class ReadOutputFilter
{
    /// <summary>
    /// Filters <paramref name="result"/> according to <paramref name="kind"/> for the given principal.
    /// Fail-closed: on any error returns a typed empty result of the expected shape.
    /// </summary>
    /// <param name="kind">Which outbound filter to apply.</param>
    /// <param name="principal">The session principal (visibility source).</param>
    /// <param name="result">The raw dispatch result.</param>
    /// <param name="logger">Optional logger for fail-closed diagnostics (server-side only).</param>
    /// <returns>The filtered result, or a typed empty result on failure.</returns>
    public static object? Filter(ReadOutputKind kind, SignalPrincipal principal, object? result, ILogger? logger)
    {
        try
        {
            return kind switch
            {
                ReadOutputKind.Accounts => FilterAccounts(principal, result as ListAccountsResponse),
                ReadOutputKind.Groups => FilterGroups(principal, result as ListGroupsResponse),
                _ => result,
            };
        }
        catch (Exception ex)
        {
            // W22: fail-closed — краще порожньо, ніж нефільтровано.
            logger?.LogError(ex, "Outbound-фільтр {Kind} кинув виняток — fail-closed (порожній результат)", kind);
            return EmptyFor(kind);
        }
    }

    /// <summary>
    /// Filters an account listing to the accounts the principal may see (R3.5 own-linked private;
    /// R3.6 admin sees all). A <c>null</c> response yields an empty listing (fail-closed).
    /// </summary>
    /// <param name="principal">The session principal.</param>
    /// <param name="response">The raw account listing.</param>
    /// <returns>The filtered account listing.</returns>
    public static ListAccountsResponse FilterAccounts(SignalPrincipal principal, ListAccountsResponse? response)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (response is null)
            return new ListAccountsResponse([]);

        // R3.6: admin (та Фаза-1 full-access) бачить усі акаунти.
        if (principal.HasFullAccess || principal.IsAdmin)
            return response;

        // R3.5: інакше — лише власні акаунти principal'а.
        var visible = response.Items.Where(a => principal.CanSeeAccount(a.Number)).ToList();
        return new ListAccountsResponse(visible);
    }

    /// <summary>
    /// Filters a group listing to the principal's claimed/own groups (R3.2, closes G9). A <c>null</c>
    /// response yields an empty listing (fail-closed).
    /// </summary>
    /// <param name="principal">The session principal.</param>
    /// <param name="response">The raw group listing.</param>
    /// <returns>The filtered group listing.</returns>
    public static ListGroupsResponse FilterGroups(SignalPrincipal principal, ListGroupsResponse? response)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (response is null)
            return new ListGroupsResponse([]);

        // Фаза-1 full-access бачить усі групи; інакше — ВИКЛЮЧНО claim'нуті/власні (admin — без винятку).
        if (principal.HasFullAccess)
            return response;

        var visible = response.Items.Where(g => principal.CanSeeGroup(g.Id)).ToList();
        return new ListGroupsResponse(visible);
    }

    // Типізований порожній результат очікуваної форми (fail-closed повернення).
    private static object? EmptyFor(ReadOutputKind kind) => kind switch
    {
        ReadOutputKind.Accounts => new ListAccountsResponse([]),
        ReadOutputKind.Groups => new ListGroupsResponse([]),
        _ => null, // недосяжно: chokepoint кличе фільтр лише для Accounts/Groups
    };
}

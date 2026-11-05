using SignalCliNet.WsRpcServer.Model;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

/// <summary>
/// Командна поверхня для автентифікованих identity (add-team-notify). Caller identity береться з principal,
/// ніколи з аргумента; права (учасник / team-admin своєї команди) перевіряє <c>TeamService</c>.
/// </summary>
/// <remarks>
/// Помилки: немає прав → <c>-32001</c>; ціль не з команди caller-а → <c>-32602</c> «not found» (анти-oracle);
/// останній team-admin → <c>-32013</c>.
/// </remarks>
public interface ISignalTeamRpc : IRpcService
{
    /// <summary>
    /// Team-admin видає інвайт у свою команду: кожне з <paramref name="maxRedemptions"/> погашень створює
    /// окреме identity з роллю <paramref name="teamRole"/> (дефолт <c>member</c>).
    /// </summary>
    Task<CreateInviteResponse> CreateTeamInvite(
        int maxRedemptions = 1, int? ttlSeconds = null, string? teamRole = null,
        CancellationToken cancellationToken = default);

    /// <summary>Активні учасники власної команди (лише team-admin).</summary>
    Task<ListTeamMembersResponse> ListTeamMembers(CancellationToken cancellationToken = default);

    /// <summary>Team-admin змінює роль учасника своєї команди; останнього team-admin не знімеш (<c>-32013</c>).</summary>
    Task<TeamMemberInfo> SetTeamRole(string identityId, string teamRole, CancellationToken cancellationToken = default);

    /// <summary>Видаляє учасника (себе — будь-хто, іншого — team-admin); останнього team-admin — ні (<c>-32013</c>).</summary>
    Task<RevokeIdentityResponse> RemoveTeamMember(string identityId, CancellationToken cancellationToken = default);

    /// <summary>Активні alias-прив'язки власної команди (будь-який учасник).</summary>
    Task<ListTeamBindingsResponse> ListTeamBindings(CancellationToken cancellationToken = default);

    /// <summary>Team-admin відкликає alias-прив'язку своєї команди.</summary>
    Task<RevokeBindingResponse> RevokeTeamBinding(string alias, CancellationToken cancellationToken = default);
}

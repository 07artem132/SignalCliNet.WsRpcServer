namespace SignalCliNet.WsRpcServer.Model;

/// <summary>Результат admin <c>createTeam</c> (add-team-notify).</summary>
/// <param name="TeamId">Opaque id нової команди.</param>
/// <param name="Name">Назва команди.</param>
public sealed record CreateTeamResponse(string TeamId, string Name);

/// <summary>Команда в admin <c>listTeams</c>.</summary>
/// <param name="TeamId">Opaque id команди.</param>
/// <param name="Name">Назва.</param>
/// <param name="ActiveMembers">Скільки активних учасників.</param>
/// <param name="ActiveTeamAdmins">Скільки активних team-admin; 0 = команда «без адміна» (потрібен break-glass).</param>
public sealed record TeamInfo(string TeamId, string Name, int ActiveMembers, int ActiveTeamAdmins);

/// <summary>Результат admin <c>listTeams</c>.</summary>
/// <param name="Teams">Усі команди.</param>
public sealed record ListTeamsResponse(IReadOnlyList<TeamInfo> Teams);

/// <summary>Активний учасник команди.</summary>
/// <param name="IdentityId">Opaque identity id (ніколи не номер).</param>
/// <param name="TeamRole"><c>team-admin</c> або <c>member</c>.</param>
public sealed record TeamMemberInfo(string IdentityId, string TeamRole);

/// <summary>Результат <c>listTeamMembers</c> (лише власна команда).</summary>
/// <param name="TeamId">Команда caller-а.</param>
/// <param name="Members">Активні учасники.</param>
public sealed record ListTeamMembersResponse(string TeamId, IReadOnlyList<TeamMemberInfo> Members);

/// <summary>Активна командна alias-прив'язка.</summary>
/// <param name="Alias">Alias (<c>all</c>, <c>admin</c>, <c>dir:&lt;id&gt;</c>, <c>login:&lt;id&gt;</c>).</param>
/// <param name="GroupId">Група, у яку веде alias.</param>
/// <param name="ExpiresAt">Backstop-термін; перепривʼяжіть до спливання.</param>
public sealed record TeamBindingInfo(string Alias, string GroupId, DateTimeOffset ExpiresAt);

/// <summary>Результат <c>listTeamBindings</c> (лише власна команда).</summary>
/// <param name="TeamId">Команда caller-а.</param>
/// <param name="Bindings">Активні alias-прив'язки.</param>
public sealed record ListTeamBindingsResponse(string TeamId, IReadOnlyList<TeamBindingInfo> Bindings);

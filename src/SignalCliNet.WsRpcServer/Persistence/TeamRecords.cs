namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>Ролі учасника команди (add-team-notify).</summary>
public static class TeamRoles
{
    /// <summary>Адмін команди: видає інвайти у свою команду, керує ролями й командними alias.</summary>
    public const string TeamAdmin = "team-admin";

    /// <summary>Звичайний учасник команди: шле на командні alias, не керує командою.</summary>
    public const string Member = "member";

    /// <summary>Чи є <paramref name="role"/> відомою командною роллю.</summary>
    public static bool IsValid(string? role) =>
        string.Equals(role, TeamAdmin, StringComparison.Ordinal) || string.Equals(role, Member, StringComparison.Ordinal);
}

/// <summary>
/// Команда (add-team-notify): група identity (пристроїв), що ділять командні alias-прив'язки груп.
/// </summary>
/// <param name="Id">Opaque id команди (CSPRNG hex).</param>
/// <param name="Name">Людська назва команди (не PII, задає admin шлюзу).</param>
/// <param name="CreatedAt">Час створення (UTC).</param>
public sealed record TeamRecord(string Id, string Name, DateTimeOffset CreatedAt);

/// <summary>
/// Членство identity у команді. Identity належить не більше ніж одній команді (PK = identity_id).
/// </summary>
/// <param name="IdentityId">Identity учасника.</param>
/// <param name="TeamId">Команда.</param>
/// <param name="TeamRole"><see cref="TeamRoles.TeamAdmin"/> або <see cref="TeamRoles.Member"/>.</param>
/// <param name="Revoked">Членство відкликано (revoke identity / видалення з команди).</param>
/// <param name="CreatedAt">Час вступу (UTC).</param>
public sealed record TeamMemberRecord(
    string IdentityId,
    string TeamId,
    string TeamRole,
    bool Revoked,
    DateTimeOffset CreatedAt);

/// <summary>
/// Командна alias-прив'язка групи (add-team-notify): <c>(team, alias) → group</c> з anchor-моделлю T2.
/// Один активний рядок на <c>(team, alias)</c>; перепривʼязка замінює групу атомарно.
/// </summary>
/// <param name="TeamId">Команда-власник.</param>
/// <param name="Alias">Псевдонім (<c>all</c>, <c>admin</c>, <c>dir:&lt;id&gt;</c>, <c>login:&lt;id&gt;</c>).</param>
/// <param name="GroupId">Група, яку зафіксував сканер.</param>
/// <param name="AnchorAci">ACI вставника коду (перевіряється на членство при кожному send).</param>
/// <param name="BoundBy">Identity team-admin, що запросив claim.</param>
/// <param name="ExpiresAt">Backstop-TTL прив'язки.</param>
/// <param name="Revoked">Прив'язку відкликано (anchor вийшов / revoke).</param>
/// <param name="CreatedAt">Час створення (UTC).</param>
public sealed record TeamBindingRecord(
    string TeamId,
    string Alias,
    string GroupId,
    string AnchorAci,
    string BoundBy,
    DateTimeOffset ExpiresAt,
    bool Revoked,
    DateTimeOffset CreatedAt);

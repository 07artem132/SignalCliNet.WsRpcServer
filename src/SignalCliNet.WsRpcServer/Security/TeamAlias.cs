using System.Text.RegularExpressions;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Командні alias прив'язок груп (add-team-notify): <c>all</c>, <c>admin</c>, <c>dir:&lt;id&gt;</c>,
/// <c>login:&lt;id&gt;</c>. У <c>sendTextMessage</c> адресуються як recipient <c>alias:&lt;alias&gt;</c>.
/// </summary>
/// <remarks>
/// <c>&lt;id&gt;</c> — непрозорий ідентифікатор консьюмера (напрям, логін), 1..64 символи
/// <c>[A-Za-z0-9_-]</c>. Шлюз його не тлумачить; правило вузьке навмисно, щоб alias не зливався з
/// номером/UUID/username/group-id і не ніс довільного тексту.
/// </remarks>
public static partial class TeamAlias
{
    /// <summary>Префікс alias-recipient у <c>sendTextMessage</c>.</summary>
    public const string RecipientPrefix = "alias:";

    [GeneratedRegex(@"^(all|admin|(dir|login):[A-Za-z0-9_-]{1,64})$", RegexOptions.CultureInvariant)]
    private static partial Regex AliasRegex();

    /// <summary>Чи <paramref name="alias"/> — валідний командний alias.</summary>
    public static bool IsValid(string? alias) => alias is not null && AliasRegex().IsMatch(alias);

    /// <summary>Чи recipient має форму <c>alias:…</c> (без перевірки валідності самого alias).</summary>
    public static bool IsAliasRecipient(string? recipient) =>
        recipient is not null && recipient.StartsWith(RecipientPrefix, StringComparison.Ordinal);

    /// <summary>Дістає alias із recipient <c>alias:&lt;alias&gt;</c>; <c>false</c>, якщо форма чи alias невалідні.</summary>
    public static bool TryParseRecipient(string? recipient, out string alias)
    {
        alias = string.Empty;
        if (!IsAliasRecipient(recipient))
            return false;

        var candidate = recipient![RecipientPrefix.Length..];
        if (!IsValid(candidate))
            return false;

        alias = candidate;
        return true;
    }
}

/// <summary>JSON-RPC error codes командної поверхні (add-team-notify).</summary>
/// <remarks>
/// <c>-32013</c> — зміна залишила б команду без активного team-admin. Сусіди: <c>-32007</c> send-gate,
/// <c>-32008</c> quota, <c>-32009</c> service restarting, <c>-32010</c>..<c>-32012</c> fail-path.
/// </remarks>
public static class TeamErrorCodes
{
    /// <summary>Відмова: останнього team-admin не можна зняти чи видалити.</summary>
    public const int LastTeamAdmin = -32013;

    /// <summary>Повідомлення для <see cref="LastTeamAdmin"/>.</summary>
    public const string LastTeamAdminMessage = "The team must keep at least one team-admin.";
}

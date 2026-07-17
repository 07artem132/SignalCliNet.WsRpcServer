using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>
/// Versioned schema migration runner keyed on <c>PRAGMA user_version</c> (G4). Applies every
/// migration newer than the database's current version inside a transaction, in order.
/// </summary>
/// <remarks>
/// Нові зміни (authn/admission/group-claim) додають свої міграції у кінець <see cref="Migrations"/>
/// зі зростаючим номером версії — цей раннер лишається їхнім спільним механізмом.
/// </remarks>
internal static class SchemaMigrator
{
    /// <summary>The highest schema version this build knows about.</summary>
    public static int LatestVersion => Migrations[^1].Version;

    // Впорядкований список міграцій. Кожна виконується рівно один раз, коли user_version < Version.
    private static readonly (int Version, string Sql)[] Migrations =
    [
        (1,
            """
            CREATE TABLE identities (
                id          TEXT PRIMARY KEY,
                role        TEXT NOT NULL,
                created_at  TEXT NOT NULL
            ) STRICT;

            CREATE TABLE identity_accounts (
                identity_id TEXT NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                account     TEXT NOT NULL,
                PRIMARY KEY (identity_id, account)
            ) STRICT;

            CREATE TABLE account_bindings (
                account            TEXT PRIMARY KEY,
                owning_identity_id TEXT NOT NULL REFERENCES identities(id) ON DELETE CASCADE,
                is_private         INTEGER NOT NULL,
                created_at         TEXT NOT NULL
            ) STRICT;

            CREATE INDEX ix_account_bindings_identity ON account_bindings(owning_identity_id);
            """),
    ];

    /// <summary>Migrates <paramref name="connection"/> up to <see cref="LatestVersion"/>.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="logger">Logger for migration progress.</param>
    /// <returns>The resulting schema version.</returns>
    public static int Migrate(SqliteConnection connection, ILogger logger)
    {
        var current = GetUserVersion(connection);

        foreach (var (version, sql) in Migrations)
        {
            if (current >= version)
                continue;

            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            SetUserVersion(connection, transaction, version);
            transaction.Commit();

            logger.LogInformation("Застосовано міграцію durable-схеми → user_version={Version}.", version);
            current = version;
        }

        return current;
    }

    /// <summary>Reads <c>PRAGMA user_version</c>.</summary>
    public static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // user_version не приймає параметра — version є довіреним int із нашого списку міграцій.
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }
}

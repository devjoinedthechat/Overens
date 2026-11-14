using System.Reflection;
using Npgsql;

namespace Overens.Data;

/// <summary>
/// Applies the numbered SQL files in <c>Schema</c>, each once and in order, as the database's
/// owner. The schema is plain SQL because roles, row-level security and triggers are the
/// point of it.
/// </summary>
public static class Migrator
{
    public static async Task MigrateAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        await Run(connection, "create table if not exists schema_migrations (name text primary key, applied_at timestamptz not null default now())", cancellationToken);

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            // One migrator at a time, so that two services starting together do not both apply a file.
            await Run(connection, "select pg_advisory_xact_lock(7262026)", cancellationToken);
            await using var applied = new NpgsqlCommand("select 1 from schema_migrations where name = $1", connection) { Parameters = { new() { Value = resource } } };
            if (await applied.ExecuteScalarAsync(cancellationToken) is null)
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                await Run(connection, await reader.ReadToEndAsync(cancellationToken), cancellationToken);
                await using var record = new NpgsqlCommand("insert into schema_migrations (name) values ($1)", connection) { Parameters = { new() { Value = resource } } };
                await record.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
    }

    /// <summary>Gives the two run-time roles their passwords. Kept out of the schema so no password is in a file.</summary>
    public static async Task SetRolePasswordsAsync(string ownerConnectionString, string apiPassword, string workerPassword, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var (role, password) in new[] { ("overens_api", apiPassword), ("overens_worker", workerPassword) })
        {
            // ALTER ROLE takes no parameters, so the password is quoted by the server's own function.
            await using var quote = new NpgsqlCommand("select format('alter role %I password %L', $1, $2)", connection) { Parameters = { new() { Value = role }, new() { Value = password } } };
            await Run(connection, (string)(await quote.ExecuteScalarAsync(cancellationToken))!, cancellationToken);
        }
    }

    private static async Task Run(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

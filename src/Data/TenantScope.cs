using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Overens.Data;

/// <summary>
/// Opens the database for one tenant. Everything done through it happens in one transaction
/// in which <c>app.tenant_id</c> is set, so row-level security shows that tenant's rows and
/// no others. There is no way to query without naming a tenant.
/// </summary>
public sealed class TenantScope(NpgsqlDataSource dataSource)
{
    public async Task<TResult> RunAsync<TResult>(string tenantId, Func<OverensDb, Task<TResult>> work, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(work);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var scope = new NpgsqlCommand("select set_config('app.tenant_id', $1, true)", connection, transaction) { Parameters = { new() { Value = tenantId } } })
        {
            await scope.ExecuteNonQueryAsync(cancellationToken);
        }

        var options = new DbContextOptionsBuilder<OverensDb>()
            .UseNpgsql(connection)
            .Options;
        await using var db = new OverensDb(options);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        var result = await work(db);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task RunAsync(string tenantId, Func<OverensDb, Task> work, CancellationToken cancellationToken = default) =>
        RunAsync(tenantId, async db =>
        {
            await work(db);
            return true;
        }, cancellationToken);
}

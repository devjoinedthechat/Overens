using Npgsql;
using Testcontainers.PostgreSql;

namespace Overens.Data.Tests;

/// <summary>A real Postgres in a container, migrated, with the two run-time roles able to sign in.</summary>
public sealed class Database : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18.3").Build();

    public string Owner => container.GetConnectionString();

    public NpgsqlDataSource Api { get; private set; } = null!;

    public NpgsqlDataSource Worker { get; private set; } = null!;

    public TenantScope AsApi => new(Api);

    public TenantScope AsWorker => new(Worker);

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await Migrator.MigrateAsync(Owner);
        // Applying it again changes nothing.
        await Migrator.MigrateAsync(Owner);
        await Migrator.SetRolePasswordsAsync(Owner, "api-test", "worker-test");
        Api = NpgsqlDataSource.Create(As("overens_api", "api-test"));
        Worker = NpgsqlDataSource.Create(As("overens_worker", "worker-test"));
    }

    public async Task DisposeAsync()
    {
        await Api.DisposeAsync();
        await Worker.DisposeAsync();
        await container.DisposeAsync();
    }

    private string As(string role, string password) =>
        new NpgsqlConnectionStringBuilder(Owner) { Username = role, Password = password }.ConnectionString;
}

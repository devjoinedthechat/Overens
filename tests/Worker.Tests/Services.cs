using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Overens.Data;
using Overens.Worker;
using Testcontainers.PostgreSql;

namespace Overens.Worker.Tests;

/// <summary>A real Postgres and a real Durable Task Scheduler (its emulator), each in a container.</summary>
public sealed class Services : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18.3").Build();
    private readonly IContainer scheduler = new ContainerBuilder(Emulator)
        .WithPortBinding(8080, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8080))
        .Build();

    /// <summary>
    /// Pinned by digest: the image's version tags are not in order (v0.0.10 is older than
    /// v0.0.9 and does not start), and "latest" would change under the tests.
    /// </summary>
    public const string Emulator = "mcr.microsoft.com/dts/dts-emulator@sha256:82ceb7df2fdfc2e22b0013c7b87b7a8e443bcd220d4fb37c32ced8ee23438719";

    private ServiceProvider clientServices = null!;

    public TenantScope AsApi { get; private set; } = null!;

    public DurableTaskClient Client { get; private set; } = null!;

    private string Scheduler => $"Endpoint=http://{scheduler.Hostname}:{scheduler.GetMappedPublicPort(8080)};TaskHub=default;Authentication=None";

    private string As(string role, string password) =>
        new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Username = role, Password = password }.ConnectionString;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), scheduler.StartAsync());
        await Migrator.MigrateAsync(postgres.GetConnectionString());
        await Migrator.SetRolePasswordsAsync(postgres.GetConnectionString(), "api-test", "worker-test");
        AsApi = new(NpgsqlDataSource.Create(As("overens_api", "api-test")));
        clientServices = new ServiceCollection()
            .AddLogging()
            .AddDurableTaskClient(client => client.UseDurableTaskScheduler(Scheduler))
            .BuildServiceProvider();
        Client = clientServices.GetRequiredService<DurableTaskClient>();
    }

    /// <summary>Starts a worker process's worth of services. Disposing it is the worker dying.</summary>
    public async Task<IHost> StartWorker(IChatClient model, string modelId = "stand-in", decimal dailyBudgetDollars = 20m, Action<IServiceCollection>? configure = null, IChatClient? secondModel = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddIngestionWorker(As("overens_worker", "worker-test"), Scheduler, model, modelId, dailyBudgetDollars, secondModel is null ? null : (secondModel, "second-stand-in"));
        // Look often, and take any run that has not been started, so a test need not wait.
        builder.Services.Configure<RunStarterOptions>(options => (options.Every, options.OlderThan, options.CheckEvery, options.CheckAfter) = (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)));
        configure?.Invoke(builder.Services);
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    public async Task DisposeAsync()
    {
        await clientServices.DisposeAsync();
        await Task.WhenAll(postgres.DisposeAsync().AsTask(), scheduler.DisposeAsync().AsTask());
    }
}

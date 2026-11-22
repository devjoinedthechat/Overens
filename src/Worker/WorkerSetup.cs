using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.AI;
using Npgsql;
using Overens.Data;
using Overens.Gateway;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Pipeline;
using Overens.Ingestion.Reading;

namespace Overens.Worker;

public static class WorkerSetup
{
    /// <summary>Registers the pipeline's steps and the durable worker that runs them.</summary>
    /// <param name="services">The host's services.</param>
    /// <param name="workerDatabase">A connection string for the <c>overens_worker</c> role.</param>
    /// <param name="scheduler">The Durable Task Scheduler's connection string.</param>
    /// <param name="model">The model to extract with.</param>
    /// <param name="modelId">The model's id.</param>
    /// <param name="dailyBudgetDollars">What one company's agreements may cost to read in a day.</param>
    /// <param name="secondModel">A second model to read every agreement again, and its id, or null for one reading.</param>
    /// <returns>The same services.</returns>
    public static IServiceCollection AddIngestionWorker(this IServiceCollection services, string workerDatabase, string scheduler, IChatClient model, string modelId, decimal dailyBudgetDollars = 20m, (IChatClient Model, string Id)? secondModel = null)
    {
        services.AddOptions<RunStarterOptions>();
        services.AddSingleton(_ => NpgsqlDataSource.Create(workerDatabase));
        services.AddSingleton(provider => new TenantScope(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<ILayoutReader, PdfLayoutReader>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(provider => new IngestionSteps(
            provider.GetRequiredService<TenantScope>(),
            provider.GetRequiredService<ILayoutReader>(),
            new RuleExtractor(model, modelId),
            modelId,
            provider.GetRequiredService<TimeProvider>(),
            dailyBudgetDollars,
            secondModel is { } second ? new RuleExtractor(second.Model, second.Id) : null));
        // Runs whose start never reached the scheduler are started from here.
        services.AddDurableTaskClient(client => client.UseDurableTaskScheduler(scheduler));
        services.AddHostedService<RunStarter>();
        services.AddDurableTaskWorker(worker =>
        {
            worker.AddTasks(tasks =>
            {
                tasks.AddOrchestrator<IngestAgreement>();
                tasks.AddActivity<ReadDocument>();
                tasks.AddActivity<ExtractRules>();
                tasks.AddActivity<PublishRules>();
                tasks.AddActivity<NoteRefusal>();
                tasks.AddActivity<EndRun>();
            });
            worker.UseDurableTaskScheduler(scheduler);
        });
        return services;
    }

    /// <summary>
    /// The model the worker uses. With a key it is Claude, under a spending cap, and nothing
    /// it answers is kept outside the database. Without one, only the answers recorded for
    /// the evals can be given, which is enough to ingest the agreements in the corpus.
    /// </summary>
    /// <param name="failover">
    /// Whether another model may answer when this one cannot. Never for a second reader,
    /// whose reading must be another model's.
    /// </param>
    public static IChatClient Model(string? apiKey, string recordings, decimal capDollars, bool failover = true) =>
        string.IsNullOrWhiteSpace(apiKey)
            ? new ReplayChatClient(null, recordings)
            : new MeteredChatClient(Claude.Create(apiKey, Claude.Background, failover: failover), new Spend(), capDollars);
}

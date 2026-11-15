using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Options;
using Npgsql;
using Overens.Ingestion.Pipeline;

namespace Overens.Worker;

/// <summary>How often the starter looks, and how long a run must have waited before it is taken.</summary>
public sealed class RunStarterOptions
{
    public TimeSpan Every { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan OlderThan { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Starts runs that were stored and never started: the API was stopped, or could not reach
/// the scheduler, between the two steps. It looks every few seconds for runs that have
/// waited longer than a moment. Starting a run twice is harmless: the run's id is the
/// orchestration's id, and the scheduler refuses a second one.
/// </summary>
public sealed partial class RunStarter(NpgsqlDataSource database, DurableTaskClient scheduler, TimeProvider clock, IOptions<RunStarterOptions> options, ILogger<RunStarter> logger) : BackgroundService
{
    private readonly TimeSpan every = options.Value.Every;
    private readonly TimeSpan olderThan = options.Value.OlderThan;

    /// <summary>How many times this starter has looked for runs, whether or not it found any.</summary>
    public int Looks { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StartWaitingRuns(stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The database or the scheduler is away. The runs are still there next time.
                CouldNotLook(logger, error);
            }

            Looks++;
            await Task.Delay(every, clock, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task StartWaitingRuns(CancellationToken cancellationToken)
    {
        var waiting = new List<RunRef>();
        await using (var find = database.CreateCommand("select tenant_id, id from runs_to_start($1)"))
        {
            find.Parameters.Add(new() { Value = clock.GetUtcNow() - olderThan });
            await using var rows = await find.ExecuteReaderAsync(cancellationToken);
            while (await rows.ReadAsync(cancellationToken))
            {
                waiting.Add(new(rows.GetString(0), rows.GetString(1)));
            }
        }

        foreach (var run in waiting)
        {
            try
            {
                await scheduler.ScheduleNewOrchestrationInstanceAsync(PipelineContract.Orchestration, run, new StartOrchestrationOptions(InstanceId: run.RunId), cancellationToken);
                Started(logger, run.RunId);
            }
            catch (Exception refused) when (refused is not OperationCanceledException)
            {
                // Refused because it was started after all, and only the note that it was is missing? Then note it.
                // Anything else: leave the run for the next look.
                if (await scheduler.GetInstanceAsync(run.RunId, cancellationToken) is null)
                {
                    throw;
                }
            }

            await using var mark = database.CreateCommand("select mark_dispatched($1, $2)");
            mark.Parameters.Add(new() { Value = run.RunId });
            mark.Parameters.Add(new() { Value = clock.GetUtcNow() });
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started run {RunId}, which had been stored and not started")]
    private static partial void Started(ILogger logger, string runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not look for runs to start")]
    private static partial void CouldNotLook(ILogger logger, Exception error);
}

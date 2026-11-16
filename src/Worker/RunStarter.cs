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

    /// <summary>How often open runs are checked against their pipelines.</summary>
    public TimeSpan CheckEvery { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a run must have been with the pipeline before it is checked. A pipeline just started may not be visible yet.</summary>
    public TimeSpan CheckAfter { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Starts runs that were stored and never started: the API was stopped, or could not reach
/// the scheduler, between the two steps. It looks every few seconds for runs that have
/// waited longer than a moment. Starting a run twice is harmless: the run's id is the
/// orchestration's id, and the scheduler refuses a second one.
///
/// It also ends runs whose pipeline is gone. A run is open until its pipeline ends it. A
/// pipeline that failed for good, or that the scheduler no longer has, will never do that,
/// so the run is ended here, as failed, with the reason. An open run is looked at in its
/// turn, the one looked at longest ago first.
/// </summary>
public sealed partial class RunStarter(NpgsqlDataSource database, DurableTaskClient scheduler, IngestionSteps steps, TimeProvider clock, IOptions<RunStarterOptions> options, ILogger<RunStarter> logger) : BackgroundService
{
    public const string PipelineGone = "The pipeline that was carrying this run stopped before it finished. Upload the agreement again.";

    private readonly TimeSpan every = options.Value.Every;
    private readonly TimeSpan olderThan = options.Value.OlderThan;
    private readonly TimeSpan checkEvery = options.Value.CheckEvery;
    private readonly TimeSpan checkAfter = options.Value.CheckAfter;
    private DateTimeOffset checkedLast = DateTimeOffset.MinValue;

    /// <summary>How many times this starter has looked for runs, whether or not it found any.</summary>
    public int Looks { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StartWaitingRuns(stoppingToken);
                if (clock.GetUtcNow() - checkedLast >= checkEvery)
                {
                    checkedLast = clock.GetUtcNow();
                    await EndRunsWithoutAPipeline(stoppingToken);
                }
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

    private async Task EndRunsWithoutAPipeline(CancellationToken cancellationToken)
    {
        var open = new List<RunRef>();
        await using (var find = database.CreateCommand("select tenant_id, id from runs_to_check($1, $2, 50)"))
        {
            find.Parameters.Add(new() { Value = clock.GetUtcNow() - checkAfter });
            find.Parameters.Add(new() { Value = clock.GetUtcNow() });
            await using var rows = await find.ExecuteReaderAsync(cancellationToken);
            while (await rows.ReadAsync(cancellationToken))
            {
                open.Add(new(rows.GetString(0), rows.GetString(1)));
            }
        }

        foreach (var run in open)
        {
            var pipeline = await scheduler.GetInstanceAsync(run.RunId, cancellationToken);
            // Running, or waiting for a reviewer: the run is where it should be.
            if (pipeline is { RuntimeStatus: OrchestrationRuntimeStatus.Running or OrchestrationRuntimeStatus.Pending or OrchestrationRuntimeStatus.Suspended })
            {
                continue;
            }

            // Gone, failed or ended without ending the run. EndAsync leaves a run alone that has ended in the meantime.
            await steps.EndAsync(run, "failed", PipelineGone, cancellationToken);
            Ended(logger, run.RunId, pipeline?.RuntimeStatus.ToString() ?? "unknown to the scheduler");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ended run {RunId} as failed: its pipeline is {State}")]
    private static partial void Ended(ILogger logger, string runId, string state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Started run {RunId}, which had been stored and not started")]
    private static partial void Started(ILogger logger, string runId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not look for runs to start")]
    private static partial void CouldNotLook(ILogger logger, Exception error);
}

using Microsoft.DurableTask;
using Overens.Ingestion.Pipeline;

namespace Overens.Worker;

/// <summary>What a reviewer decided about a run as a whole.</summary>
public sealed record ReviewDecision(bool Publish);

/// <summary>
/// One agreement's way from a stored PDF to a published rule set. The orchestration is
/// durable: its progress is kept by the scheduler, so a worker that dies is replaced and the
/// run carries on from the last finished step. Between extraction and publication it waits
/// for a person, for as long as that takes.
/// </summary>
public sealed class IngestAgreement : TaskOrchestrator<RunRef, string>
{
    public const string DecisionEvent = "decision";

    /// <summary>Model calls and the database can fail for a moment; these steps are tried again.</summary>
    private static readonly TaskOptions Retried = new(new TaskRetryOptions(new RetryPolicy(
        maxNumberOfAttempts: 4,
        firstRetryInterval: TimeSpan.FromSeconds(2),
        backoffCoefficient: 2)));

    public override async Task<string> RunAsync(TaskOrchestrationContext context, RunRef run)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            // A document that cannot be read fails the run at once. Only passing trouble is tried again.
            if (await context.CallActivityAsync<string?>(nameof(ReadDocument), run, Retried) is { } unreadable)
            {
                await context.CallActivityAsync(nameof(EndRun), new EndRunInput(run, "failed", unreadable));
                return "failed";
            }

            await context.CallActivityAsync(nameof(ExtractRules), run, Retried);
        }
        catch (TaskFailedException failure)
        {
            await context.CallActivityAsync(nameof(EndRun), new EndRunInput(run, "failed", failure.FailureDetails.ErrorMessage));
            return "failed";
        }

        while (true)
        {
            var decision = await context.WaitForExternalEvent<ReviewDecision>(DecisionEvent);
            if (!decision.Publish)
            {
                await context.CallActivityAsync(nameof(EndRun), new EndRunInput(run, "rejected", null));
                return "rejected";
            }

            try
            {
                await context.CallActivityAsync<int>(nameof(PublishRules), run);
                return "published";
            }
            catch (TaskFailedException)
            {
                // The database refused: no approval on record, or rules still undecided.
                // The run stays open and waits for a decision that can be carried out.
            }
        }
    }
}

public sealed record EndRunInput(RunRef Run, string Status, string? Failure);

/// <summary>Reads the document. Returns why it could not be read, or null when it was.</summary>
public sealed class ReadDocument(IngestionSteps steps) : TaskActivity<RunRef, string?>
{
    public override async Task<string?> RunAsync(TaskActivityContext context, RunRef run)
    {
        try
        {
            await steps.ReadAsync(run);
            return null;
        }
        catch (UnreadableDocumentException unreadable)
        {
            return unreadable.Message;
        }
    }
}

public sealed class ExtractRules(IngestionSteps steps) : TaskActivity<RunRef, bool>
{
    public override async Task<bool> RunAsync(TaskActivityContext context, RunRef run)
    {
        await steps.ExtractAsync(run);
        return true;
    }
}

public sealed partial class PublishRules(IngestionSteps steps, ILogger<PublishRules> logger) : TaskActivity<RunRef, int>
{
    public override async Task<int> RunAsync(TaskActivityContext context, RunRef run)
    {
        ArgumentNullException.ThrowIfNull(run);
        try
        {
            return await steps.PublishAsync(run);
        }
        catch (Exception refusal)
        {
            // The run stays open, so the reason must not be lost.
            Refused(logger, run.RunId, refusal);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId} was not published")]
    private static partial void Refused(ILogger logger, string runId, Exception refusal);
}

public sealed class EndRun(IngestionSteps steps) : TaskActivity<EndRunInput, bool>
{
    public override async Task<bool> RunAsync(TaskActivityContext context, EndRunInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        await steps.EndAsync(input.Run, input.Status, input.Failure);
        return true;
    }
}

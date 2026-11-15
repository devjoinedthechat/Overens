using Microsoft.DurableTask;
using Overens.Gateway;
using Overens.Ingestion.Pipeline;

namespace Overens.Worker;

/// <summary>
/// One agreement's way from a stored PDF to a published rule set. The orchestration is
/// durable: its progress is kept by the scheduler, so a worker that dies is replaced and the
/// run carries on from the last finished step. Between extraction and publication it waits
/// for a person, for as long as that takes.
/// </summary>
[DurableTask(PipelineContract.Orchestration)]
public sealed class IngestAgreement : TaskOrchestrator<RunRef, string>
{
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
                await context.CallActivityAsync(nameof(EndRun), new EndRunInput(run, "failed", unreadable), Retried);
                return "failed";
            }

            if (await context.CallActivityAsync<string?>(nameof(ExtractRules), run, Retried) is { } refused)
            {
                await context.CallActivityAsync(nameof(EndRun), new EndRunInput(run, "failed", refused), Retried);
                return "failed";
            }
        }
        catch (TaskFailedException failure)
        {
            await context.CallActivityAsync(nameof(EndRun), new EndRunInput(run, "failed", failure.FailureDetails.ErrorMessage), Retried);
            return "failed";
        }

        while (true)
        {
            var decision = await context.WaitForExternalEvent<ReviewDecision>(PipelineContract.DecisionEvent);
            if (!decision.Publish)
            {
                await context.CallActivityAsync(nameof(EndRun), new EndRunInput(run, "rejected", null), Retried);
                return "rejected";
            }

            // The run stays open when it cannot be published, and waits for a decision that can be
            // carried out. Why it could not is written on the run, where the reviewer sees it.
            string? refusal;
            try
            {
                refusal = await context.CallActivityAsync<string?>(nameof(PublishRules), run, Retried);
            }
            catch (TaskFailedException failure)
            {
                refusal = $"The rules could not be published now: {failure.FailureDetails.ErrorMessage}";
            }

            if (refusal is null)
            {
                return "published";
            }

            await context.CallActivityAsync(nameof(NoteRefusal), new EndRunInput(run, "awaiting_review", refusal), Retried);
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

/// <summary>Extracts the rules. Returns why it would not, or null when it did.</summary>
public sealed class ExtractRules(IngestionSteps steps) : TaskActivity<RunRef, string?>
{
    public override async Task<string?> RunAsync(TaskActivityContext context, RunRef run)
    {
        try
        {
            await steps.ExtractAsync(run);
            return null;
        }
        catch (Exception final) when (final is BudgetReachedException or UnreadableDocumentException or IncompleteAnswerException)
        {
            // Asking again would end the same way, so the run fails now and says why.
            return final.Message;
        }
    }
}

/// <summary>
/// Publishes the run's approved rules. Returns why they were refused, or null when they
/// were published. A refusal is an answer: the database or the checks said no, and will say
/// no again. Anything else (the database being away) is thrown, and tried again.
/// </summary>
public sealed partial class PublishRules(IngestionSteps steps, ILogger<PublishRules> logger) : TaskActivity<RunRef, string?>
{
    public override async Task<string?> RunAsync(TaskActivityContext context, RunRef run)
    {
        ArgumentNullException.ThrowIfNull(run);
        try
        {
            await steps.PublishAsync(run);
            return null;
        }
        catch (Exception refusal) when (refusal is PublishRefusedException || refusal.InnerException is PublishRefusedException)
        {
            Refused(logger, run.RunId, refusal);
            return refusal.Message;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId} was not published")]
    private static partial void Refused(ILogger logger, string runId, Exception refusal);
}

/// <summary>Writes on an open run why it could not be published.</summary>
public sealed class NoteRefusal(IngestionSteps steps) : TaskActivity<EndRunInput, bool>
{
    public override async Task<bool> RunAsync(TaskActivityContext context, EndRunInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        await steps.NoteAsync(input.Run, input.Failure ?? "");
        return true;
    }
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

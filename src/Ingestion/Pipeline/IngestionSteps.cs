using Microsoft.EntityFrameworkCore;
using Overens.Data;
using Overens.Gateway;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Reading;
using Overens.Rules;

namespace Overens.Ingestion.Pipeline;

/// <summary>Which run, for which tenant.</summary>
public sealed record RunRef(string TenantId, string RunId);

/// <summary>What a reviewer decided about a run as a whole.</summary>
public sealed record ReviewDecision(bool Publish);

/// <summary>
/// The names the API and the worker must agree on to reach the same pipeline. They are
/// written once, here, where both can see them.
/// </summary>
public static class PipelineContract
{
    /// <summary>The orchestration that takes one agreement from a stored PDF to a published rule set.</summary>
    public const string Orchestration = "IngestAgreement";

    /// <summary>The event that carries a reviewer's <see cref="ReviewDecision"/> to a waiting run.</summary>
    public const string DecisionEvent = "decision";
}

/// <summary>
/// The pipeline's steps, each safe to run again: a step that is repeated after a crash finds
/// its own earlier work and does not do it twice. They run as the worker's database role,
/// which can store what was read and proposed but cannot approve anything.
/// </summary>
/// <param name="secondReader">
/// Another model's extractor, or null. When given, it reads every agreement too, and each
/// rule it differs on is flagged for the reviewer (see <see cref="SecondReading"/>). It must
/// not fail over to the first model, or the second reading would be the first one again.
/// </param>
public sealed class IngestionSteps(TenantScope database, ILayoutReader reader, RuleExtractor extractor, string modelId, TimeProvider clock, decimal dailyBudgetDollars = 20m, RuleExtractor? secondReader = null)
{
    public const int MaxPages = 500;

    /// <summary>
    /// The longest agreement sent to a model, in characters. Longer than this and it would
    /// not fit beside the task and the answer, and every call would be refused.
    /// </summary>
    public const int MaxCharacters = 400_000;

    /// <summary>Reads the run's document into numbered clauses and stores them.</summary>
    public async Task ReadAsync(RunRef run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        using var span = Telemetry.Start("agreement.read", run.RunId).With(Field.Tenant, run.TenantId);
        var document = await database.RunAsync(run.TenantId, async db =>
        {
            var row = await db.Runs.SingleAsync(row => row.Id == run.RunId, cancellationToken);
            return row.Clauses is null
                ? await db.Documents.Where(document => document.Sha256 == row.DocumentSha256).Select(document => document.Content).SingleAsync(cancellationToken)
                : null;
        }, cancellationToken);
        if (document is null)
        {
            return;
        }

        ReadAgreement read;
        try
        {
            var pages = await reader.ReadAsync(document, cancellationToken);
            if (pages.Count > MaxPages)
            {
                throw new UnreadableDocumentException($"The document has {pages.Count} pages. At most {MaxPages} are read.");
            }

            read = ClauseSegmenter.Segment(pages);
        }
        catch (Exception error) when (error is not (OperationCanceledException or UnreadableDocumentException))
        {
            // A file that is cut short, encrypted or not really a PDF. Whatever the reader made of it, trying again will not help.
            throw new UnreadableDocumentException("The file could not be read as a PDF.", error);
        }

        if (read.Sections.Count == 0)
        {
            throw new UnreadableDocumentException("No numbered clauses were found in the document.");
        }

        await database.RunAsync(run.TenantId, async db =>
        {
            var row = await db.Runs.SingleAsync(row => row.Id == run.RunId, cancellationToken);
            (row.Title, row.Clauses) = (read.Title, RuleJson.Write(read));
        }, cancellationToken);
        span.With(Field.Clauses, read.Pages.Count);
    }

    /// <summary>Asks the model for the rules, checks them, stores them, and hands the run to a reviewer.</summary>
    public async Task ExtractAsync(RunRef run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        using var span = Telemetry.Start("agreement.extract", run.RunId).With(Field.Tenant, run.TenantId).With(Field.PromptVersion, ExtractionPrompt.Version);
        var clauses = await database.RunAsync(run.TenantId, async db =>
        {
            var row = await db.Runs.SingleAsync(row => row.Id == run.RunId, cancellationToken);
            return row.Status == "started" ? row.Clauses : null;
        }, cancellationToken);
        if (clauses is null)
        {
            return;
        }

        // One company cannot spend without limit: what its runs have cost since midnight is checked before a model is called.
        var now = clock.GetUtcNow();
        var midnight = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var spent = await database.RunAsync(run.TenantId, db =>
            db.Database.SqlQuery<long>($"select tenant_spend_since({midnight}) as \"Value\"").SingleAsync(cancellationToken), cancellationToken);
        if (spent >= dailyBudgetDollars * 1_000_000m)
        {
            throw new BudgetReachedException($"The company's daily budget for reading agreements (${dailyBudgetDollars}) is used up. Try again tomorrow.");
        }

        var agreement = RuleJson.Read<ReadAgreement>(clauses);
        if (ExtractionPrompt.Document(agreement).Length > MaxCharacters)
        {
            throw new UnreadableDocumentException($"The agreement is longer than a model can read in one piece ({MaxCharacters:N0} characters).");
        }

        using var usage = Usage.Begin();
        ExtractionResult extraction;
        try
        {
            extraction = await extractor.ExtractAsync(agreement, cancellationToken);
            if (extraction.Unreadable.Count > 0)
            {
                // Part of an answer is not a set of rules. A reviewer must never see a rule set with a kind silently missing.
                throw new InvalidOperationException($"The model's answer for {string.Join(", ", extraction.Unreadable.Select(problem => problem.RuleId))} could not be read.");
            }

            if (secondReader is not null)
            {
                extraction = await ReadAgainAsync(extraction, agreement, cancellationToken);
            }
        }
        catch (Exception) when (usage.Calls > 0)
        {
            // An attempt that failed was paid for all the same, and counts against the company's budget.
            await database.RunAsync(run.TenantId, async db => Charge(await db.Runs.SingleAsync(row => row.Id == run.RunId, CancellationToken.None), usage), CancellationToken.None);
            throw;
        }

        await database.RunAsync(run.TenantId, async db =>
        {
            var row = await db.Runs.SingleAsync(row => row.Id == run.RunId, cancellationToken);
            Charge(row, usage);
            // The status is checked again inside the transaction that writes, so two copies of this step cannot both store rules.
            if (row.Status != "started")
            {
                return;
            }

            db.ProposedRules.AddRange(extraction.Rules.Select(proposed => new ProposedRuleRow
            {
                RunId = run.RunId,
                RuleId = proposed.Rule.Id,
                TenantId = run.TenantId,
                Rule = RuleJson.Write(proposed.Rule),
                AlsoFrom = RuleJson.Write(proposed.AlsoFrom),
                Problems = RuleJson.Write(proposed.Problems),
            }));
            // The model recorded is the one that answered. Under failover that is not always the one asked for.
            var answeredBy = usage.Models.Count == 0 ? modelId : string.Join(", ", usage.Models);
            (row.Model, row.PromptVersion, row.Gaps, row.Status) = (answeredBy, ExtractionPrompt.Version, RuleJson.Write(extraction.Gaps), "awaiting_review");
        }, cancellationToken);
        span.With(Field.Model, usage.Models.Count == 0 ? modelId : string.Join(',', usage.Models))
            .With(Field.RulesProposed, extraction.Rules.Count)
            .With(Field.RulesFlagged, extraction.Rules.Count(rule => rule.Problems.Count > 0))
            .With(Field.Gaps, extraction.Gaps.Count)
            .With(Field.ModelCalls, usage.Calls)
            .With(Field.InputTokens, usage.InputTokens)
            .With(Field.OutputTokens, usage.OutputTokens)
            .With(Field.CostMicros, usage.CostMicros);
    }

    /// <summary>
    /// Publishes the approved rules as the agreement's next version. The database refuses
    /// unless a person has approved the run; this step adds one check of its own, that the
    /// approved rules together still form a usable rule set.
    /// </summary>
    /// <returns>The published version.</returns>
    /// <exception cref="PublishRefusedException">The checks or the database said no, and will say no again.</exception>
    public async Task<int> PublishAsync(RunRef run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        using var span = Telemetry.Start("agreement.publish", run.RunId).With(Field.Tenant, run.TenantId);
        int version;
        try
        {
            version = await PublishApprovedAsync(run, cancellationToken);
        }
        catch (Exception refused) when (Raised(refused) is { } said)
        {
            // The database's own "no": no approval on record, rules still undecided, nothing approved.
            throw new PublishRefusedException(said, refused);
        }

        span.With(Field.Version, version);
        return version;
    }

    /// <summary>Writes on a run that is still open why it could not be published.</summary>
    public Task NoteAsync(RunRef run, string refusal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        return database.RunAsync(run.TenantId, async db =>
        {
            var row = await db.Runs.SingleAsync(row => row.Id == run.RunId, cancellationToken);
            if (row.Status == "awaiting_review")
            {
                row.Failure = refusal;
            }
        }, cancellationToken);
    }

    /// <summary>The message of an exception the database raised on purpose, wherever it sits in the chain.</summary>
    private static string? Raised(Exception error)
    {
        for (var inner = error; inner is not null; inner = inner.InnerException)
        {
            if (inner is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.RaiseException } raised)
            {
                return raised.MessageText;
            }
        }

        return null;
    }

    /// <summary>What a piece of work cost is added to what the run has cost so far: a run can be tried more than once.</summary>
    /// <summary>
    /// The second reading. A rule it differs on is flagged. If it cannot be had, the run goes
    /// on without it and says so: a missing second reading is a gap for the reviewer, not a
    /// reason to stop.
    /// </summary>
    private async Task<ExtractionResult> ReadAgainAsync(ExtractionResult first, ReadAgreement agreement, CancellationToken cancellationToken)
    {
        RuleProblem Missing(string why) => new("second_reading", "second_reading_missing", $"The agreement was read by one model only: {why}");
        try
        {
            var second = await secondReader!.ExtractAsync(agreement, cancellationToken);
            if (second.Unreadable.Count > 0)
            {
                return first with { Gaps = [.. first.Gaps, Missing("the second model's answer could not be read.")] };
            }

            return SecondReading.Apply(first, second);
        }
        catch (Exception error) when (error is not OperationCanceledException and not BudgetReachedException)
        {
            return first with { Gaps = [.. first.Gaps, Missing($"the second model failed ({error.GetType().Name}).")] };
        }
    }

    private static void Charge(IngestionRunRow row, Usage usage) =>
        (row.ModelCalls, row.InputTokens, row.OutputTokens, row.CostMicros) =
            ((row.ModelCalls ?? 0) + usage.Calls, (row.InputTokens ?? 0) + usage.InputTokens, (row.OutputTokens ?? 0) + usage.OutputTokens, (row.CostMicros ?? 0) + usage.CostMicros);

    private Task<int> PublishApprovedAsync(RunRef run, CancellationToken cancellationToken) =>
        database.RunAsync(run.TenantId, async db =>
        {
            var approved = await db.ProposedRules
                .Where(rule => rule.RunId == run.RunId && rule.Decision == "approved")
                .Select(rule => rule.Rule)
                .ToListAsync(cancellationToken);
            var problems = RuleSetValidator.Validate(new(run.RunId, 0, [.. approved.Select(RuleJson.Read<Rule>)]));
            if (problems.Count > 0)
            {
                throw new PublishRefusedException($"The approved rules do not form a usable rule set: {string.Join("; ", problems.Select(problem => $"{problem.RuleId} {problem.Code}"))}.");
            }

            return await db.Database.SqlQuery<int>($"select publish_rule_set({run.RunId}, {clock.GetUtcNow()}) as \"Value\"").SingleAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>Ends a run that will not produce a rule set, saying why.</summary>
    public Task EndAsync(RunRef run, string status, string? failure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        return database.RunAsync(run.TenantId, async db =>
        {
            var row = await db.Runs.SingleAsync(row => row.Id == run.RunId, cancellationToken);
            if (row.Status is "started" or "awaiting_review")
            {
                (row.Status, row.Failure, row.FinishedAt) = (status, failure, clock.GetUtcNow());
            }
        }, cancellationToken);
    }
}

/// <summary>The run's rules were not published, and asking again will not change that.</summary>
public sealed class PublishRefusedException : Exception
{
    public PublishRefusedException(string message)
        : base(message)
    {
    }

    public PublishRefusedException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public PublishRefusedException()
    {
    }
}

/// <summary>The company has spent its budget for the day. Trying again today will not help.</summary>
public sealed class BudgetReachedException : Exception
{
    public BudgetReachedException(string message)
        : base(message)
    {
    }

    public BudgetReachedException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public BudgetReachedException()
    {
    }
}

/// <summary>The document cannot be turned into clauses. Trying again will not help.</summary>
public sealed class UnreadableDocumentException : Exception
{
    public UnreadableDocumentException(string message)
        : base(message)
    {
    }

    public UnreadableDocumentException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public UnreadableDocumentException()
    {
    }
}

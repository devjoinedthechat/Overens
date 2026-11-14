using Microsoft.EntityFrameworkCore;
using Overens.Data;
using Overens.Gateway;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Reading;
using Overens.Rules;

namespace Overens.Ingestion.Pipeline;

/// <summary>Which run, for which tenant.</summary>
public sealed record RunRef(string TenantId, string RunId);

/// <summary>
/// The pipeline's steps, each safe to run again: a step that is repeated after a crash finds
/// its own earlier work and does not do it twice. They run as the worker's database role,
/// which can store what was read and proposed but cannot approve anything.
/// </summary>
public sealed class IngestionSteps(TenantScope database, ILayoutReader reader, RuleExtractor extractor, string modelId, TimeProvider clock, decimal dailyBudgetDollars = 20m)
{
    public const int MaxPages = 500;

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

        using var usage = Usage.Begin();
        var extraction = await extractor.ExtractAsync(RuleJson.Read<ReadAgreement>(clauses), cancellationToken);
        if (extraction.Unreadable.Count > 0)
        {
            // Part of an answer is not a set of rules. A reviewer must never see a rule set with a kind silently missing.
            throw new InvalidOperationException($"The model's answer for {string.Join(", ", extraction.Unreadable.Select(problem => problem.RuleId))} could not be read.");
        }

        await database.RunAsync(run.TenantId, async db =>
        {
            var row = await db.Runs.SingleAsync(row => row.Id == run.RunId, cancellationToken);
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
            (row.ModelCalls, row.InputTokens, row.OutputTokens, row.CostMicros) = (usage.Calls, usage.InputTokens, usage.OutputTokens, usage.CostMicros);
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
    public async Task<int> PublishAsync(RunRef run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        using var span = Telemetry.Start("agreement.publish", run.RunId).With(Field.Tenant, run.TenantId);
        var version = await database.RunAsync(run.TenantId, async db =>
        {
            var approved = await db.ProposedRules
                .Where(rule => rule.RunId == run.RunId && rule.Decision == "approved")
                .Select(rule => rule.Rule)
                .ToListAsync(cancellationToken);
            var problems = RuleSetValidator.Validate(new(run.RunId, 0, [.. approved.Select(RuleJson.Read<Rule>)]));
            if (problems.Count > 0)
            {
                throw new InvalidOperationException($"The approved rules do not form a usable rule set: {string.Join("; ", problems.Select(problem => $"{problem.RuleId} {problem.Code}"))}.");
            }

            return await db.Database.SqlQuery<int>($"select publish_rule_set({run.RunId}, {clock.GetUtcNow()}) as \"Value\"").SingleAsync(cancellationToken);
        }, cancellationToken);
        span.With(Field.Version, version);
        return version;
    }

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

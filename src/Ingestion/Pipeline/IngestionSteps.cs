using Microsoft.EntityFrameworkCore;
using Overens.Data;
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
public sealed class IngestionSteps(TenantScope database, ILayoutReader reader, RuleExtractor extractor, string modelId, TimeProvider clock)
{
    /// <summary>Reads the run's document into numbered clauses and stores them.</summary>
    public async Task ReadAsync(RunRef run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
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

        var read = ClauseSegmenter.Segment(await reader.ReadAsync(document, cancellationToken));
        if (read.Sections.Count == 0)
        {
            throw new UnreadableDocumentException("No numbered clauses were found in the document.");
        }

        await database.RunAsync(run.TenantId, async db =>
        {
            var row = await db.Runs.SingleAsync(row => row.Id == run.RunId, cancellationToken);
            (row.Title, row.Clauses) = (read.Title, RuleJson.Write(read));
        }, cancellationToken);
    }

    /// <summary>Asks the model for the rules, checks them, stores them, and hands the run to a reviewer.</summary>
    public async Task ExtractAsync(RunRef run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var clauses = await database.RunAsync(run.TenantId, async db =>
        {
            var row = await db.Runs.SingleAsync(row => row.Id == run.RunId, cancellationToken);
            return row.Status == "started" ? row.Clauses : null;
        }, cancellationToken);
        if (clauses is null)
        {
            return;
        }

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
            (row.Model, row.PromptVersion, row.Gaps, row.Status) = (modelId, ExtractionPrompt.Version, RuleJson.Write(extraction.Gaps), "awaiting_review");
        }, cancellationToken);
    }

    /// <summary>
    /// Publishes the approved rules as the agreement's next version. The database refuses
    /// unless a person has approved the run; this step adds one check of its own, that the
    /// approved rules together still form a usable rule set.
    /// </summary>
    /// <returns>The published version.</returns>
    public Task<int> PublishAsync(RunRef run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        return database.RunAsync(run.TenantId, async db =>
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

/// <summary>The document cannot be turned into clauses. Trying again will not help.</summary>
public sealed class UnreadableDocumentException(string message) : Exception(message);

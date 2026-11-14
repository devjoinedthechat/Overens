using System.Security.Cryptography;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Overens.Data;
using Overens.Ingestion.Pipeline;
using Overens.Ingestion.Tests;
using Overens.Rules;
using Overens.Synthetic;

namespace Overens.Worker.Tests;

public sealed class PipelineTests(Services stack) : IClassFixture<Services>
{
    private static readonly SyntheticAgreement Agreement = Corpus.Generate(Corpus.Agreements[2]);
    private static readonly byte[] Pdf = AgreementPdf.Render(Agreement.Document);
    private static int counter;

    private static StandInModel Model(Action<string, System.Text.Json.Nodes.JsonArray>? spoil = null) =>
        new(Agreement.Document, Agreement.Truth, spoil);

    /// <summary>What the API does when a specialist uploads an agreement: store it, open a run, start the pipeline.</summary>
    private async Task<RunRef> Upload(byte[] pdf)
    {
        var number = Interlocked.Increment(ref counter);
        var run = new RunRef($"tenant-{number}", $"run-{number}");
        var sha = Convert.ToHexStringLower(SHA256.HashData(pdf));
        await stack.AsApi.RunAsync(run.TenantId, db =>
        {
            db.Documents.Add(new() { TenantId = run.TenantId, Sha256 = sha, FileName = "agreement.pdf", Content = pdf, UploadedBy = "anna", UploadedAt = DateTimeOffset.UtcNow });
            db.Runs.Add(new() { Id = run.RunId, TenantId = run.TenantId, AgreementId = "lager", DocumentSha256 = sha, Status = "started", CreatedAt = DateTimeOffset.UtcNow });
            return Task.CompletedTask;
        });
        await stack.Client.ScheduleNewOrchestrationInstanceAsync(nameof(IngestAgreement), run, new StartOrchestrationOptions(InstanceId: run.RunId));
        return run;
    }

    private async Task<IngestionRunRow> Until(RunRef run, Func<IngestionRunRow, bool> reached)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            var row = await stack.AsApi.RunAsync(run.TenantId, db => db.Runs.SingleAsync());
            if (reached(row))
            {
                return row;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("The run did not get there within a minute.");
    }

    /// <summary>What the API does when a specialist has reviewed every rule and approves the run.</summary>
    private async Task Review(RunRef run, bool approve)
    {
        await stack.AsApi.RunAsync(run.TenantId, async db =>
        {
            foreach (var rule in await db.ProposedRules.ToListAsync())
            {
                (rule.Decision, rule.DecidedBy, rule.DecidedAt) = (rule.Problems == "[]" ? "approved" : "rejected", "anna", DateTimeOffset.UtcNow);
            }

        });

        // The approval is a request of its own, made after the decisions.
        if (approve)
        {
            await stack.AsApi.RunAsync(run.TenantId, db =>
            {
                db.Approvals.Add(new() { RunId = run.RunId, TenantId = run.TenantId, ApprovedBy = "anna", ApprovedAt = DateTimeOffset.UtcNow });
                return Task.CompletedTask;
            });
        }
    }

    private Task Decide(RunRef run, bool publish) =>
        stack.Client.RaiseEventAsync(run.RunId, IngestAgreement.DecisionEvent, new ReviewDecision(publish));

    [Fact]
    public async Task A_pdf_becomes_a_published_rule_set_once_a_person_approves_it()
    {
        using var worker = await stack.StartWorker(Model());
        var run = await Upload(Pdf);

        var waiting = await Until(run, row => row.Status == "awaiting_review");
        Assert.Equal(Agreement.Document.Title, waiting.Title);
        Assert.Equal(Agreement.Truth.Rules.Count, await stack.AsApi.RunAsync(run.TenantId, db => db.ProposedRules.CountAsync()));
        Assert.Equal(0, await stack.AsApi.RunAsync(run.TenantId, db => db.RuleSets.CountAsync()));

        await Review(run, approve: true);
        await Decide(run, publish: true);
        await Until(run, row => row.Status == "published");

        var set = await stack.AsApi.RunAsync(run.TenantId, db => db.RuleSets.SingleAsync());
        var published = new RuleSet(set.AgreementId, set.Version, RuleJson.Read<List<Rule>>(set.Rules));
        Assert.Equal(("lager", 1, "anna"), (set.AgreementId, set.Version, set.ApprovedBy));
        Assert.Equal(
            RuleJson.Write(Agreement.Truth.Rules.OrderBy(rule => rule.Id, StringComparer.Ordinal)),
            RuleJson.Write(published.Rules.OrderBy(rule => rule.Id, StringComparer.Ordinal)));
        Assert.Empty(RuleSetValidator.Validate(published));
    }

    [Fact]
    public async Task A_run_survives_the_worker_dying_while_it_waits_for_review()
    {
        var model = Model();
        var run = await Upload(Pdf);
        using (var first = await stack.StartWorker(model))
        {
            await Until(run, row => row.Status == "awaiting_review");
            await first.StopAsync();
        }

        // Nobody is running the pipeline. The review happens anyway.
        await Review(run, approve: true);
        await Decide(run, publish: true);
        await Task.Delay(1500);
        Assert.Equal("awaiting_review", (await Until(run, _ => true)).Status);

        using var second = await stack.StartWorker(model);
        await Until(run, row => row.Status == "published");

        // The second worker carried on from the review. It did not read or extract again.
        Assert.Equal(6, model.Requests.Count);
        Assert.Equal(1, await stack.AsApi.RunAsync(run.TenantId, db => db.RuleSets.CountAsync()));
    }

    [Fact]
    public async Task Telling_the_pipeline_to_publish_does_nothing_without_an_approval_on_record()
    {
        using var worker = await stack.StartWorker(Model());
        var run = await Upload(Pdf);
        await Until(run, row => row.Status == "awaiting_review");

        // Every rule is decided, but no person has approved the run.
        await Review(run, approve: false);
        await Decide(run, publish: true);
        await Task.Delay(3000);

        Assert.Equal("awaiting_review", (await Until(run, _ => true)).Status);
        Assert.Equal(0, await stack.AsApi.RunAsync(run.TenantId, db => db.RuleSets.CountAsync()));

        // The run is still open: an approval and a second decision publish it.
        await stack.AsApi.RunAsync(run.TenantId, db =>
        {
            db.Approvals.Add(new() { RunId = run.RunId, TenantId = run.TenantId, ApprovedBy = "anna", ApprovedAt = DateTimeOffset.UtcNow });
            return Task.CompletedTask;
        });
        await Decide(run, publish: true);
        await Until(run, row => row.Status == "published");
    }

    [Fact]
    public async Task A_rule_the_checks_flag_is_left_out_of_what_is_published()
    {
        using var worker = await stack.StartWorker(Model((kind, rules) =>
        {
            if (kind == "holiday_allowance")
            {
                rules[0]!["percent"] = 13.5m;
            }
        }));
        var run = await Upload(Pdf);
        await Until(run, row => row.Status == "awaiting_review");

        await Review(run, approve: true);
        await Decide(run, publish: true);
        await Until(run, row => row.Status == "published");

        var rules = RuleJson.Read<List<Rule>>(await stack.AsApi.RunAsync(run.TenantId, db => db.RuleSets.Select(set => set.Rules).SingleAsync()));
        Assert.Equal(Agreement.Truth.Rules.Count - 1, rules.Count);
        Assert.DoesNotContain(rules, rule => rule is HolidayAllowanceRule);
    }

    [Fact]
    public async Task A_rejected_run_publishes_nothing()
    {
        using var worker = await stack.StartWorker(Model());
        var run = await Upload(Pdf);
        await Until(run, row => row.Status == "awaiting_review");

        await Decide(run, publish: false);

        Assert.NotNull((await Until(run, row => row.Status == "rejected")).FinishedAt);
        Assert.Equal(0, await stack.AsApi.RunAsync(run.TenantId, db => db.RuleSets.CountAsync()));
    }

    [Fact]
    public async Task A_file_that_is_not_an_agreement_fails_the_run_and_says_why()
    {
        using var worker = await stack.StartWorker(Model());
        var notAnAgreement = AgreementPdf.Render(Agreement.Document with { Sections = [], Protocols = [] });

        var run = await Upload(notAnAgreement);

        var failed = await Until(run, row => row.Status == "failed");
        Assert.Contains("No numbered clauses", failed.Failure, StringComparison.Ordinal);
        Assert.Equal(0, await stack.AsApi.RunAsync(run.TenantId, db => db.ProposedRules.CountAsync()));
    }

    [Fact]
    public async Task A_model_that_fails_for_a_moment_is_asked_again()
    {
        var inner = Model();
        using var worker = await stack.StartWorker(new FailsFirst(inner, failures: 2));
        var run = await Upload(Pdf);

        await Until(run, row => row.Status == "awaiting_review");

        Assert.Equal(Agreement.Truth.Rules.Count, await stack.AsApi.RunAsync(run.TenantId, db => db.ProposedRules.CountAsync()));
    }

    /// <summary>A model whose first calls fail, as a provider does when it is overloaded.</summary>
    private sealed class FailsFirst(IChatClient inner, int failures) : DelegatingChatClient(inner)
    {
        private int remaining = failures;

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Interlocked.Decrement(ref remaining) >= 0
                ? throw new HttpRequestException("529 overloaded")
                : base.GetResponseAsync(messages, options, cancellationToken);
    }
}

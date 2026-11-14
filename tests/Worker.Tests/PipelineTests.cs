using System.Security.Cryptography;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Overens.Data;
using Overens.Gateway;
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
    private async Task<RunRef> Upload(byte[] pdf, string? tenant = null, bool start = true)
    {
        var number = Interlocked.Increment(ref counter);
        var run = new RunRef(tenant ?? $"tenant-{number}", $"run-{number}");
        var sha = Convert.ToHexStringLower(SHA256.HashData(pdf));
        await stack.AsApi.RunAsync(run.TenantId, async db =>
        {
            if (!await db.Documents.AnyAsync(document => document.Sha256 == sha))
            {
                db.Documents.Add(new() { TenantId = run.TenantId, Sha256 = sha, FileName = "agreement.pdf", Content = pdf, UploadedBy = "anna", UploadedAt = DateTimeOffset.UtcNow });
            }

            db.Runs.Add(new() { Id = run.RunId, TenantId = run.TenantId, AgreementId = "lager", DocumentSha256 = sha, Status = "started", CreatedAt = DateTimeOffset.UtcNow });
        });
        if (start)
        {
            await stack.Client.ScheduleNewOrchestrationInstanceAsync(nameof(IngestAgreement), run, new StartOrchestrationOptions(InstanceId: run.RunId));
        }

        return run;
    }

    private async Task<IngestionRunRow> Until(RunRef run, Func<IngestionRunRow, bool> reached)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            var row = await stack.AsApi.RunAsync(run.TenantId, db => db.Runs.SingleAsync(row => row.Id == run.RunId));
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

    public static TheoryData<string, string> DamagedFiles => new()
    {
        { "cut short", "could not be read as a PDF" },
        { "only a header", "could not be read as a PDF" },
        { "scrambled", "could not be read as a PDF" },
    };

    [Theory]
    [MemberData(nameof(DamagedFiles))]
    public async Task A_damaged_pdf_fails_the_run_at_once_with_a_reason_and_never_reaches_the_model(string damage, string reason)
    {
        var model = Model();
        using var worker = await stack.StartWorker(model);
        var file = damage switch
        {
            "cut short" => Pdf[..(Pdf.Length / 3)],
            "only a header" => "%PDF-1.7\n"u8.ToArray(),
            _ => [.. "%PDF-1.7\n"u8, .. Pdf.Skip(9).Select((value, at) => (byte)(value ^ (at % 251)))],
        };

        var started = DateTimeOffset.UtcNow;
        var run = await Upload(file);
        var failed = await Until(run, row => row.Status == "failed");

        Assert.Contains(reason, failed.Failure, StringComparison.Ordinal);
        Assert.Empty(model.Requests);
        // At once: an unreadable file is not retried with waits in between.
        Assert.InRange(DateTimeOffset.UtcNow - started, TimeSpan.Zero, TimeSpan.FromSeconds(20));
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

    [Fact]
    public async Task A_run_that_was_stored_and_never_started_is_started_by_the_worker()
    {
        using var worker = await stack.StartWorker(Model());

        // The API stored the run and stopped, or could not reach the scheduler, before telling the pipeline.
        var run = await Upload(Pdf, start: false);

        var waiting = await Until(run, row => row.Status == "awaiting_review");
        Assert.NotNull(waiting.DispatchedAt);
        // Looked at again and again, it is started once.
        await Task.Delay(3000);
        Assert.Equal(Agreement.Truth.Rules.Count, await stack.AsApi.RunAsync(run.TenantId, db => db.ProposedRules.CountAsync()));
    }

    [Fact]
    public async Task What_a_run_cost_is_recorded_and_a_company_that_has_spent_its_days_budget_is_refused()
    {
        // Each call uses 100,000 tokens in and 1,000 out: at Haiku's prices just over a cent, so about six cents an agreement.
        using var model = new MeteredChatClient(new Uses(Model(), 100_000, 1_000), new Spend(), capDollars: 5m);
        using var worker = await stack.StartWorker(model, Gateway.Models.Haiku, dailyBudgetDollars: 0.05m);
        var tenant = $"budget-{Guid.NewGuid():N}";

        var first = await Until(await Upload(Pdf, tenant), row => row.Status == "awaiting_review");
        Assert.Equal((6, 600_000L, 6_000L, 63_000L, Gateway.Models.Haiku), (first.ModelCalls, first.InputTokens, first.OutputTokens, first.CostMicros, first.Model));

        var second = await Until(await Upload(Pdf, tenant), row => row.Status is "failed" or "awaiting_review");
        Assert.Equal("failed", second.Status);
        Assert.Contains("daily budget", second.Failure, StringComparison.Ordinal);
        Assert.Null(second.ModelCalls);

        // Another company's budget is its own.
        await Until(await Upload(Pdf), row => row.Status == "awaiting_review");
    }

    [Fact]
    public async Task When_the_model_asked_for_is_down_the_other_one_reads_the_agreement_and_the_run_says_so()
    {
        using var model = new MeteredChatClient(Claude.Wrap(new DownFor(Gateway.Models.Sonnet, new Uses(Model(), 1000, 100))), new Spend(), capDollars: 5m);
        using var worker = await stack.StartWorker(model, Gateway.Models.Sonnet);

        var run = await Until(await Upload(Pdf), row => row.Status == "awaiting_review");

        Assert.Equal(Gateway.Models.Haiku, run.Model);
        Assert.Equal(Agreement.Truth.Rules.Count, await stack.AsApi.RunAsync(run.TenantId, db => db.ProposedRules.CountAsync()));
    }

    /// <summary>A model that reports how many tokens each call used.</summary>
    private sealed class Uses(IChatClient inner, long input, long output) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            response.Usage = new() { InputTokenCount = input, OutputTokenCount = output };
            return response;
        }
    }

    /// <summary>A provider one of whose models cannot be reached.</summary>
    private sealed class DownFor(string modelId, IChatClient inner) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            options?.ModelId == modelId ? throw new HttpRequestException("529 overloaded") : base.GetResponseAsync(messages, options, cancellationToken);
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

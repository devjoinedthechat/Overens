using System.Security.Cryptography;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Overens.Data;
using Overens.Gateway;
using Overens.Ingestion.Pipeline;
using Overens.Ingestion.Reading;
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
    private async Task<RunRef> Upload(byte[] pdf, string? tenant = null, bool start = true, bool dispatched = false)
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

            db.Runs.Add(new()
            {
                Id = run.RunId,
                TenantId = run.TenantId,
                AgreementId = "lager",
                DocumentSha256 = sha,
                Status = "started",
                CreatedAt = DateTimeOffset.UtcNow,
                // As the API records it: handed to the pipeline now, or (to play a lost pipeline) a while ago whether or not it was.
                DispatchedAt = dispatched ? DateTimeOffset.UtcNow.AddMinutes(-5) : start ? DateTimeOffset.UtcNow : null,
            });
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
        stack.Client.RaiseEventAsync(run.RunId, PipelineContract.DecisionEvent, new ReviewDecision(publish));

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

        // The pipeline tried, was refused, and wrote on the run why. The run is still open.
        var refused = await Until(run, row => row.Failure is not null);
        Assert.Contains("has not been approved by a person", refused.Failure, StringComparison.Ordinal);
        Assert.Equal("awaiting_review", refused.Status);
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
    public async Task A_rule_a_second_reading_differs_on_is_put_to_the_reviewer()
    {
        // The overtime percentages exchanged: both are printed in the clause, so no check on the text can see it.
        static void Exchanged(string kind, System.Text.Json.Nodes.JsonArray rules)
        {
            if (kind == "overtime")
            {
                var tiers = rules[0]!["tiers"]!.AsArray();
                (tiers[0]!["percent"], tiers[^1]!["percent"]) = (tiers[^1]!["percent"]!.DeepClone(), tiers[0]!["percent"]!.DeepClone());
            }
        }

        using var alone = await stack.StartWorker(Model(Exchanged));
        var once = await Upload(Pdf);
        await Until(once, row => row.Status == "awaiting_review");
        Assert.Equal("[]", await ProblemsOf(once, "overtime"));
        await alone.StopAsync();

        using var twice = await stack.StartWorker(Model(Exchanged), secondModel: Model());
        var run = await Upload(Pdf);
        var row = await Until(run, row => row.Status == "awaiting_review");

        Assert.Contains(Ingestion.Extraction.SecondReading.Code, await ProblemsOf(run, "overtime"), StringComparison.Ordinal);
        Assert.Equal("[]", await ProblemsOf(run, "holiday_allowance"));
        Assert.DoesNotContain("second_reading_missing", row.Gaps, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_second_reading_fails_the_run_goes_on_and_the_reviewer_is_told()
    {
        using var worker = await stack.StartWorker(Model(), secondModel: new FailsFirst(Model(), failures: 1000));
        var run = await Upload(Pdf);

        var row = await Until(run, row => row.Status == "awaiting_review");

        Assert.Contains("second_reading_missing", row.Gaps, StringComparison.Ordinal);
        Assert.Equal("[]", await ProblemsOf(run, "overtime"));
    }

    private Task<string> ProblemsOf(RunRef run, string kind) =>
        stack.AsApi.RunAsync(run.TenantId, db => db.ProposedRules.Where(rule => rule.RunId == run.RunId && rule.RuleId.StartsWith(kind)).Select(rule => rule.Problems).FirstAsync());

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
        var reader = new CountingReader();
        using var worker = await stack.StartWorker(model, configure: services => services.AddSingleton<ILayoutReader>(reader));
        var file = damage switch
        {
            "cut short" => Pdf[..(Pdf.Length / 3)],
            "only a header" => "%PDF-1.7\n"u8.ToArray(),
            _ => [.. "%PDF-1.7\n"u8, .. Pdf.Skip(9).Select((value, at) => (byte)(value ^ (at % 251)))],
        };

        var run = await Upload(file);
        var failed = await Until(run, row => row.Status == "failed");

        Assert.Contains(reason, failed.Failure, StringComparison.Ordinal);
        Assert.Empty(model.Requests);
        // At once: an unreadable file is read once and not tried again.
        Assert.Equal(1, reader.Reads);
    }

    /// <summary>The real reader, counting how often it is asked.</summary>
    private sealed class CountingReader : ILayoutReader
    {
        private readonly PdfLayoutReader inner = new();
        private int reads;

        public int Reads => reads;

        public Task<IReadOnlyList<LayoutPage>> ReadAsync(ReadOnlyMemory<byte> pdf, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref reads);
            return inner.ReadAsync(pdf, cancellationToken);
        }
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
    public async Task A_runs_spans_are_one_trace_and_say_nothing_of_what_the_agreement_says()
    {
        var spans = new System.Collections.Concurrent.ConcurrentBag<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStopped = spans.Add,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        using var worker = await stack.StartWorker(Model());

        var run = await Upload(Pdf);
        await Until(run, row => row.Status == "awaiting_review");
        await Review(run, approve: true);
        await Decide(run, publish: true);
        await Until(run, row => row.Status == "published");

        var mine = spans.Where(span => (string?)span.GetTagItem("overens.run") == run.RunId).ToList();
        Assert.Equal(["agreement.extract", "agreement.publish", "agreement.read"], mine.Select(span => span.OperationName).Distinct().Order());
        Assert.Single(mine.Select(span => span.TraceId).Distinct());
        var extract = mine.First(span => span.OperationName == "agreement.extract");
        Assert.Equal((13L, 0L, "stand-in"), (extract.GetTagItem("overens.rules.proposed"), extract.GetTagItem("overens.rules.flagged"), extract.GetTagItem("gen_ai.response.model")));

        // Nothing the agreement says, in any attribute of any span.
        var said = string.Join(' ', mine.SelectMany(span => span.TagObjects).Select(tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain("Tværhavn", said, StringComparison.Ordinal);
        Assert.DoesNotContain("pct.", said, StringComparison.Ordinal);
        Assert.DoesNotContain("§", said, StringComparison.Ordinal);
        Assert.All(mine.SelectMany(span => span.TagObjects), tag => Assert.True(tag.Value is long or int || ((string)tag.Value!).Length <= 100));
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
        var starter = worker.Services.GetServices<IHostedService>().OfType<RunStarter>().Single();
        for (var looks = starter.Looks; starter.Looks < looks + 3;)
        {
            await Task.Delay(100);
        }

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
        using var model = new MeteredChatClient(Claude.Wrap(new DownFor(Gateway.Models.Sonnet, new Uses(Model(), 1000, 100)), Claude.Background), new Spend(), capDollars: 5m);
        using var worker = await stack.StartWorker(model, Gateway.Models.Sonnet);

        var run = await Until(await Upload(Pdf), row => row.Status == "awaiting_review");

        Assert.Equal(Gateway.Models.Haiku, run.Model);
        Assert.Equal(Agreement.Truth.Rules.Count, await stack.AsApi.RunAsync(run.TenantId, db => db.ProposedRules.CountAsync()));
    }

    [Fact]
    public async Task A_run_whose_pipeline_is_gone_is_ended_and_a_run_that_is_waiting_for_review_is_left_alone()
    {
        using var worker = await stack.StartWorker(Model());

        // Handed to the pipeline, by the record, and the scheduler has never heard of it: the pipeline was lost.
        var lost = await Upload(Pdf, start: false, dispatched: true);
        // An ordinary run that reaches review and waits there, with its pipeline alive.
        var waiting = await Upload(Pdf);

        var ended = await Until(lost, row => row.Status == "failed");
        Assert.Equal(RunStarter.PipelineGone, ended.Failure);
        Assert.NotNull(ended.FinishedAt);

        // The waiting run has been looked at too, more than once, and is still open.
        await Until(waiting, row => row.Status == "awaiting_review" && row.CheckedAt is not null);
        var first = (await Until(waiting, _ => true)).CheckedAt;
        var later = await Until(waiting, row => row.CheckedAt > first);
        Assert.Equal(("awaiting_review", null), (later.Status, later.Failure));

        // And it can still be published.
        await Review(waiting, approve: true);
        await Decide(waiting, publish: true);
        await Until(waiting, row => row.Status == "published");
    }

    [Fact]
    public async Task An_answer_that_is_cut_off_fails_the_run_at_once_and_what_it_cost_still_counts()
    {
        using var model = new MeteredChatClient(new CutOff(new Uses(Model(), 100_000, 4_000)), new Spend(), capDollars: 5m);
        using var worker = await stack.StartWorker(model, Gateway.Models.Haiku, dailyBudgetDollars: 0.01m);
        var tenant = $"cut-{Guid.NewGuid():N}";

        var failed = await Until(await Upload(Pdf, tenant), row => row.Status is "failed" or "awaiting_review");

        Assert.Equal("failed", failed.Status);
        Assert.Contains("cut off", failed.Failure, StringComparison.Ordinal);
        // Asked once for each kind and not again: the same requests would be cut off the same way. They were paid for all the same.
        Assert.Equal((6, 600_000L, 24_000L, 72_000L), (failed.ModelCalls, failed.InputTokens, failed.OutputTokens, failed.CostMicros));
        Assert.Equal(0, await stack.AsApi.RunAsync(tenant, db => db.ProposedRules.CountAsync()));

        // The failed attempt used up the company's day: the next agreement is refused before a model is called.
        var next = await Until(await Upload(Pdf, tenant), row => row.Status is "failed" or "awaiting_review");
        Assert.Contains("daily budget", next.Failure, StringComparison.Ordinal);
    }

    /// <summary>A model every answer of which ran out of room.</summary>
    private sealed class CutOff(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            response.FinishReason = ChatFinishReason.Length;
            return response;
        }
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

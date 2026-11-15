using Microsoft.Extensions.AI;

namespace Overens.Gateway.Tests;

public sealed class GatewayTests
{
    private static ChatOptions For(string model) => new() { ModelId = model };

    [Theory]
    [InlineData("Medarbejder 010190-1234 mangler tillæg.", "Medarbejder [CPR-nummer fjernet] mangler tillæg.")]
    [InlineData("cpr 0101901234, og 010190 1234.", "cpr [CPR-nummer fjernet], og [CPR-nummer fjernet].")]
    [InlineData("Satsen er 144,45 kr. pr. 1. marts 2026, jf. § 4, stk. 1.", "Satsen er 144,45 kr. pr. 1. marts 2026, jf. § 4, stk. 1.")]
    [InlineData("Run 20261115-1 and amount 123456789012.", "Run 20261115-1 and amount 123456789012.")]
    public void A_cpr_shaped_number_is_taken_out_and_other_numbers_are_left(string text, string expected)
    {
        Assert.Equal(expected, RedactingChatClient.Redact(text));
    }

    [Fact]
    public async Task Nothing_cpr_shaped_reaches_the_model_in_a_message_or_in_a_tool_result()
    {
        var model = new Echo();
        using var client = new RedactingChatClient(model);

        await client.GetResponseAsync(
        [
            new(ChatRole.System, "Svar kort."),
            new(ChatRole.User, "Hvad skal 010190-1234 have i tillæg?"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "read_clause", new Dictionary<string, object?> { ["clause_id"] = "§ 5, stk. 2" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "Kontakt 0101901234 ved spørgsmål.")]),
        ], For(Models.Haiku));

        var sent = model.Seen.SelectMany(message => message.Contents).Select(content => content switch
        {
            TextContent text => text.Text,
            FunctionResultContent result => result.Result?.ToString(),
            _ => null,
        }).OfType<string>().ToList();
        Assert.Equal(["Svar kort.", "Hvad skal [CPR-nummer fjernet] have i tillæg?", "Kontakt [CPR-nummer fjernet] ved spørgsmål."], sent);
        // The tool call the model made itself is passed back as it was.
        Assert.Single(model.Seen.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
    }

    [Fact]
    public async Task Each_call_is_priced_and_the_next_one_is_refused_once_the_cap_is_reached()
    {
        var spend = new Spend();
        using var client = new MeteredChatClient(new Echo(inputTokens: 1_000_000, outputTokens: 100_000), spend, capDollars: 0.25m);

        await client.GetResponseAsync([new(ChatRole.User, "a")], For(Models.Haiku));
        // A million tokens in at $0.10 and a hundred thousand out at $0.50 per million.
        Assert.Equal((0.15m, 1), (spend.Dollars, spend.Calls));
        await client.GetResponseAsync([new(ChatRole.User, "b")], For(Models.Haiku));
        Assert.Equal(0.30m, spend.Dollars);

        var refused = await Assert.ThrowsAsync<BudgetExceededException>(() => client.GetResponseAsync([new(ChatRole.User, "c")], For(Models.Haiku)));
        Assert.Contains("0.25", refused.Message, StringComparison.Ordinal);
        Assert.Equal(2, spend.Calls);
    }

    [Fact]
    public async Task When_a_model_cannot_be_reached_the_other_one_answers_and_the_answer_says_which()
    {
        var provider = new Echo { Down = [Models.Sonnet] };
        using var client = new MeteredChatClient(Claude.Wrap(provider, Claude.Interactive), new Spend(), capDollars: 1m);
        using var usage = Usage.Begin();

        var response = await client.GetResponseAsync([new(ChatRole.User, "Hvad er satsen?")], For(Models.Sonnet));

        Assert.Equal(("svar: Hvad er satsen?", Models.Haiku), (response.Text, response.ModelId));
        Assert.Equal([Models.Sonnet, Models.Haiku], provider.Asked);
        // Recorded and priced as the model that answered: ten tokens each way at Haiku's prices.
        Assert.Equal([Models.Haiku], usage.Models);
        Assert.Equal((1, 6L), (usage.Calls, usage.CostMicros));

        // With both away, the failure is the caller's to handle.
        provider.Down = [Models.Sonnet, Models.Haiku];
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync([new(ChatRole.User, "a")], For(Models.Sonnet)));
    }

    [Fact]
    public async Task A_request_the_provider_refuses_as_wrong_is_not_sent_to_another_model()
    {
        var provider = new Echo { Refuses = true };
        using var client = Claude.Wrap(provider, Claude.Interactive);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetResponseAsync([new(ChatRole.User, "a")], For(Models.Sonnet)));
        Assert.Equal([Models.Sonnet], provider.Asked);

        // Nor is a call the caller itself gave up on.
        using var gaveUp = new CancellationTokenSource();
        await gaveUp.CancelAsync();
        Assert.False(FailoverChatClient.IsUnavailable(new OperationCanceledException(), gaveUp.Token));
        Assert.True(FailoverChatClient.IsUnavailable(new OperationCanceledException(), CancellationToken.None));
    }

    [Fact]
    public async Task Usage_is_counted_for_the_work_it_was_opened_around_and_for_nothing_else()
    {
        using var client = new MeteredChatClient(new Echo(inputTokens: 1000, outputTokens: 100), new Spend(), capDollars: 1m);
        await client.GetResponseAsync([new(ChatRole.User, "before")], For(Models.Sonnet));

        using (var outer = Usage.Begin())
        {
            using (var inner = Usage.Begin())
            {
                // Calls made at the same time, as the extractor makes them.
                await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.GetResponseAsync([new(ChatRole.User, "a")], For(Models.Sonnet))));
                Assert.Equal((5, 5000L, 500L, 15_000L), (inner.Calls, inner.InputTokens, inner.OutputTokens, inner.CostMicros));
            }

            await client.GetResponseAsync([new(ChatRole.User, "after")], For(Models.Haiku));
            Assert.Equal(6, outer.Calls);
            Assert.Equal([Models.Haiku, Models.Sonnet], outer.Models);
        }
    }

    [Fact]
    public void A_span_carries_ids_sizes_and_statuses_and_withholds_anything_that_reads_like_content()
    {
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        using var span = Telemetry.Start("agreement.extract", "run_0195")
            .With(Field.Tenant, "tvaerhavn-loen")
            .With(Field.Model, "claude-haiku-5-5,claude-sonnet-5-5")
            .With(Field.RulesProposed, 13)
            .With(Field.Agreement, "Feriegodtgørelsen udgør 12,5 pct. af lønnen.")
            .With(Field.Outcome, "010190-1234")
            .With(Field.Status, null);
        span.Failed(new InvalidOperationException("Clause § 8 says: Feriegodtgørelsen udgør 12,5 pct."));

        var tags = span!.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value);
        Assert.Equal("run_0195", tags["overens.run"]);
        Assert.Equal("tvaerhavn-loen", tags["overens.tenant"]);
        Assert.Equal("claude-haiku-5-5,claude-sonnet-5-5", tags["gen_ai.response.model"]);
        Assert.Equal(13L, tags["overens.rules.proposed"]);
        // A sentence and a CPR number are not labels.
        Assert.Equal(Telemetry.Withheld, tags["overens.agreement"]);
        Assert.Equal(Telemetry.Withheld, tags["overens.outcome"]);
        Assert.False(tags.ContainsKey("overens.status"));
        // A failure is named by its kind. Its message may quote what was being read.
        Assert.Equal("InvalidOperationException", span.StatusDescription);

        // Every span of a run is in one trace, whoever makes it.
        using var later = Telemetry.Start("agreement.publish", "run_0195");
        using var other = Telemetry.Start("agreement.read", "run_0196");
        Assert.Equal(span.TraceId, later!.TraceId);
        Assert.NotEqual(span.TraceId, other!.TraceId);
    }

    [Fact]
    public async Task A_call_that_cannot_be_priced_is_not_made()
    {
        var model = new Echo();
        using var client = new MeteredChatClient(model, new Spend(), capDollars: 1m);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new(ChatRole.User, "a")], For("some-other-model")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new(ChatRole.User, "a")]));
        Assert.Empty(model.Seen);
    }

    [Fact]
    public async Task A_recorded_answer_is_given_only_for_the_request_that_produced_it()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"overens-replay-{Guid.NewGuid():N}");
        try
        {
            var model = new Echo();
            using (var recording = new ReplayChatClient(model, directory))
            {
                await recording.GetResponseAsync([new(ChatRole.User, "Hvad er satsen?")], For(Models.Haiku));
                await recording.GetResponseAsync([new(ChatRole.User, "Hvad er satsen?")], For(Models.Haiku));
            }

            // Asked twice, recorded and paid for once.
            Assert.Single(model.Seen);
            Assert.Single(Directory.GetFiles(directory));
            Assert.DoesNotContain("usage", await File.ReadAllTextAsync(Directory.GetFiles(directory)[0]), StringComparison.OrdinalIgnoreCase);

            using var replaying = new ReplayChatClient(null, directory);
            Assert.Equal("svar: Hvad er satsen?", (await replaying.GetResponseAsync([new(ChatRole.User, "Hvad er satsen?")], For(Models.Haiku))).Text);
            // Another question, another model or another answer shape was never recorded.
            await Assert.ThrowsAsync<InvalidOperationException>(() => replaying.GetResponseAsync([new(ChatRole.User, "Hvad er satsen nu?")], For(Models.Haiku)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => replaying.GetResponseAsync([new(ChatRole.User, "Hvad er satsen?")], For(Models.Sonnet)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A model that repeats the last message back, and remembers what it was sent.</summary>
    private sealed class Echo(long inputTokens = 10, long outputTokens = 10) : IChatClient
    {
        public List<ChatMessage> Seen { get; } = [];

        /// <summary>The model each call asked for, in order.</summary>
        public List<string?> Asked { get; } = [];

        /// <summary>Models that cannot be reached.</summary>
        public string[] Down { get; set; } = [];

        /// <summary>Whether every request is refused as wrong.</summary>
        public bool Refuses { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Asked.Add(options?.ModelId);
            if (Refuses)
            {
                throw new ArgumentException("400 invalid_request_error");
            }

            if (Down.Contains(options?.ModelId))
            {
                throw new HttpRequestException("529 overloaded");
            }

            Seen.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"svar: {messages.Last().Text}"))
            {
                Usage = new() { InputTokenCount = inputTokens, OutputTokenCount = outputTokens },
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

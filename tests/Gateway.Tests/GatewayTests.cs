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

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
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

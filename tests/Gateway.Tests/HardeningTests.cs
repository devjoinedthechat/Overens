using System.Net;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;

namespace Overens.Gateway.Tests;

public sealed class HardeningTests
{
    private static ChatOptions For(string model, int? maxOutput = null) => new() { ModelId = model, MaxOutputTokens = maxOutput };

    [Theory]
    [InlineData("Medarbejder 010190 - 1234 mangler tillæg.", "Medarbejder [CPR-nummer fjernet] mangler tillæg.")]
    [InlineData("Født 01.01.90-1234.", "Født [CPR-nummer fjernet].")]
    [InlineData("Gælder fra 01.03.2027 og fra 1. marts 2027.", "Gælder fra 01.03.2027 og fra 1. marts 2027.")]
    [InlineData("Telefon 12 34 56 78.", "Telefon 12 34 56 78.")]
    public void A_cpr_number_written_with_spaces_or_dots_is_taken_out_and_a_date_is_left(string text, string expected)
    {
        Assert.Equal(expected, RedactingChatClient.Redact(text));
    }

    [Fact]
    public async Task Redaction_keeps_what_the_caller_attached_to_a_message_and_reads_a_tool_result_that_is_not_text()
    {
        var model = new Scripted();
        using var client = new RedactingChatClient(model);

        await client.GetResponseAsync(
        [
            new(ChatRole.User, [Claude.Cacheable("Aftalen nævner 010190-1234.")]) { AuthorName = "anna" },
            new(ChatRole.Tool, [new FunctionResultContent("call-1", new { contact = "0101901234" })]),
        ], For(Models.Haiku));

        var sent = model.Seen[0];
        Assert.Equal("anna", sent[0].AuthorName);
        var text = Assert.IsType<TextContent>(sent[0].Contents[0]);
        Assert.Equal("Aftalen nævner [CPR-nummer fjernet].", text.Text);
        // The mark that lets the provider keep the text is still on it.
        Assert.Equal(Claude.Cacheable("x").AdditionalProperties!.Keys, text.AdditionalProperties!.Keys);
        Assert.DoesNotContain("0101901234", Assert.IsType<FunctionResultContent>(sent[1].Contents[0]).Result?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Tokens_read_from_the_cache_and_written_to_it_have_their_own_prices()
    {
        var spend = new Spend();

        // 21,000 tokens in: 1,000 fresh, 10,000 read from the cache, 10,000 written to it.
        spend.Add(Models.Prices[Models.Sonnet], new()
        {
            InputTokenCount = 21_000,
            CachedInputTokenCount = 10_000,
            AdditionalCounts = new() { [ModelPrice.CacheWritten] = 10_000 },
            OutputTokenCount = 0,
        });

        Assert.Equal(((1_000 * 2m) + (10_000 * 0.2m) + (10_000 * 2.5m)) / 1_000_000m, spend.Dollars);
        Assert.Equal(21_000, spend.InputTokens);
    }

    [Fact]
    public async Task Calls_made_at_the_same_moment_cannot_all_slip_under_a_cap_only_one_of_them_fits_under()
    {
        var model = new Scripted { Hold = new() };
        var spend = new Spend();
        // One call may cost up to its 4,000 tokens of answer: a fifth of a cent at Haiku's prices, which is the whole cap.
        using var client = new MeteredChatClient(model, spend, capDollars: 0.002m);

        var first = client.GetResponseAsync([new(ChatRole.User, "a")], For(Models.Haiku, 4000));
        await Assert.ThrowsAsync<BudgetExceededException>(() => client.GetResponseAsync([new(ChatRole.User, "b")], For(Models.Haiku, 4000)));
        model.Hold.SetResult();
        await first;

        // What was set aside is given back: the next call is judged on what was really spent.
        model.Hold = null;
        await client.GetResponseAsync([new(ChatRole.User, "c")], For(Models.Haiku, 4000));
        Assert.Equal(2, spend.Calls);
    }

    [Fact]
    public async Task A_streamed_answer_is_not_given_because_it_would_not_be_priced()
    {
        using var client = new MeteredChatClient(new Scripted(), new Spend(), capDollars: 1m);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "a")], For(Models.Haiku)))
            {
            }
        });
    }

    public static TheoryData<Exception, bool> Failures => new()
    {
        { new AnthropicIOException("connection reset", new HttpRequestException("reset")), true },
        { new AnthropicOverloadedException(new HttpRequestException("529")) { StatusCode = (HttpStatusCode)529, ResponseBody = "" }, true },
        { new AnthropicRateLimitException(new HttpRequestException("429")) { StatusCode = HttpStatusCode.TooManyRequests, ResponseBody = "" }, true },
        { new AnthropicRequestTimeoutException(new HttpRequestException("408")) { StatusCode = HttpStatusCode.RequestTimeout, ResponseBody = "" }, true },
        { new AnthropicGatewayTimeoutException(new HttpRequestException("504")) { StatusCode = HttpStatusCode.GatewayTimeout, ResponseBody = "" }, true },
        { new TimeoutException(), true },
        { new AnthropicBadRequestException(new HttpRequestException("400")) { StatusCode = HttpStatusCode.BadRequest, ResponseBody = "" }, false },
        { new AnthropicUnauthorizedException(new HttpRequestException("401")) { StatusCode = HttpStatusCode.Unauthorized, ResponseBody = "" }, false },
        { new InvalidOperationException(), false },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void Only_a_failure_that_means_not_now_is_sent_to_the_other_model(Exception failure, bool unavailable)
    {
        Assert.Equal(unavailable, FailoverChatClient.IsUnavailable(failure, CancellationToken.None));
    }

    [Fact]
    public async Task A_model_that_takes_too_long_is_a_failure_and_not_the_caller_giving_up()
    {
        using var slow = new TimeoutChatClient(new Scripted { Hold = new() }, TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => slow.GetResponseAsync([new(ChatRole.User, "a")], For(Models.Haiku)));

        // The caller giving up is still the caller giving up.
        using var gaveUp = new CancellationTokenSource();
        var asked = slow.GetResponseAsync([new(ChatRole.User, "a")], For(Models.Haiku), gaveUp.Token);
        await gaveUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asked);
    }

    [Fact]
    public async Task A_slow_model_is_given_up_on_and_the_other_one_answers()
    {
        var provider = new Scripted { SlowFor = Models.Sonnet };
        using var client = Claude.Wrap(provider, TimeSpan.FromMilliseconds(50));

        var response = await client.GetResponseAsync([new(ChatRole.User, "a")], For(Models.Sonnet));

        Assert.Equal(Models.Haiku, response.ModelId);
    }

    [Fact]
    public async Task An_answer_that_was_cut_off_or_declined_is_refused_and_a_whole_one_is_not()
    {
        ChatResponse Ended(ChatFinishReason? reason) => new(new ChatMessage(ChatRole.Assistant, "{\"text\": \"Linjen")) { FinishReason = reason };

        Assert.Contains("cut off", Assert.Throws<IncompleteAnswerException>(() => Ended(ChatFinishReason.Length).RequireComplete()).Message, StringComparison.Ordinal);
        Assert.Contains("declined", Assert.Throws<IncompleteAnswerException>(() => Ended(ChatFinishReason.ContentFilter).RequireComplete()).Message, StringComparison.Ordinal);
        Ended(ChatFinishReason.Stop).RequireComplete();
        Ended(ChatFinishReason.ToolCalls).RequireComplete();
        Ended(null).RequireComplete();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_recording_keeps_how_a_cut_off_answer_ended_and_never_files_one_models_answer_under_another()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"overens-replay-{Guid.NewGuid():N}");
        try
        {
            using (var recording = new ReplayChatClient(new Scripted { Finish = ChatFinishReason.Length }, directory))
            {
                await recording.GetResponseAsync([new(ChatRole.User, "a")], For(Models.Haiku));
            }

            using var replaying = new ReplayChatClient(null, directory);
            var replayed = await replaying.GetResponseAsync([new(ChatRole.User, "a")], For(Models.Haiku));
            Assert.Equal(ChatFinishReason.Length, replayed.FinishReason);
            Assert.Throws<IncompleteAnswerException>(() => replayed.RequireComplete());

            // The same question with more room for the answer is another request: the short answer is not replayed for it.
            await Assert.ThrowsAsync<InvalidOperationException>(() => replaying.GetResponseAsync([new(ChatRole.User, "a")], For(Models.Haiku, 1500)));

            // Sonnet was asked and Haiku answered: that is not Sonnet's answer, and is not kept as it.
            using var other = new ReplayChatClient(new Scripted { AnswersAs = Models.Haiku }, directory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => other.GetResponseAsync([new(ChatRole.User, "b")], For(Models.Sonnet)));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A model that can be held, be slow for one model, end its answer a given way, or answer as another model.</summary>
    private sealed class Scripted : IChatClient
    {
        public List<List<ChatMessage>> Seen { get; } = [];

        public TaskCompletionSource? Hold { get; set; }

        public string? SlowFor { get; init; }

        public ChatFinishReason? Finish { get; init; }

        public string? AnswersAs { get; init; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen.Add([.. messages]);
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            if (SlowFor is not null && SlowFor == options?.ModelId)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new(new ChatMessage(ChatRole.Assistant, "svar"))
            {
                ModelId = AnswersAs,
                FinishReason = Finish,
                Usage = new() { InputTokenCount = 10, OutputTokenCount = 10 },
            };
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

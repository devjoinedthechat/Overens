using System.Text.Json;
using Microsoft.Extensions.AI;
using Overens.Rules;
using Overens.Rules.Tests;

namespace Overens.Agent.Tests;

public sealed class FindingExplainerTests
{
    /// <summary>The March sample with the overtime line paying 150,00 where 1.050,00 was due.</summary>
    private static readonly PayCheck Check = PayEngine.Check(
        Sample.Agreement,
        Sample.March,
        Sample.Faithful(Sample.Agreement, Sample.March, 150.00m).With(PayLineKind.Overtime, 150.00m));

    private static readonly Finding TooLow = Check.Findings.Single();

    private static Task<Explanation> Explain(string? answer, Finding? finding = null, bool fails = false) =>
        new FindingExplainer(answer is null && !fails ? null : new Says(answer, fails), "stand-in").ExplainAsync(finding ?? TooLow, Check.Expected);

    [Fact]
    public async Task Wording_that_uses_only_the_findings_numbers_is_shown()
    {
        var text = "Overarbejdstillægget er udbetalt med 150,00 kr., men skulle have været 1.050,00 kr. Der mangler 900,00 kr. "
            + "Det dækker 2,00 timer til 75,00 kr. og 6,00 timer til 150,00 kr. Reglen står i § 9, stk. 2.";

        var explanation = await Explain(text);

        Assert.Equal((text, true), (explanation.Text, explanation.ByModel));
    }

    [Theory]
    [InlineData("Der mangler 900,00 kr., svarende til 85,7 pct. af tillægget.")]
    [InlineData("Der mangler ca. 900 kr. i alt 1.200,00 kr. med pension.")]
    [InlineData("Der skulle være udbetalt 1050,50 kr.")]
    [InlineData("Der mangler 900,00 kr. Efterbetal senest om 14 dage.")]
    public async Task Wording_with_a_number_that_is_not_the_findings_is_replaced_by_the_fixed_wording(string text)
    {
        var explanation = await Explain(text);

        Assert.False(explanation.ByModel);
        Assert.Equal(
            "Overarbejdstillæg: der er udbetalt 150,00 kr., men efter overenskomsten skulle der være udbetalt 1.050,00 kr. Der mangler 900,00 kr. Se § 9, stk. 2.",
            explanation.Text);
    }

    [Fact]
    public void The_guard_says_why_it_refused_and_accepts_figures_as_the_model_was_shown_them()
    {
        Assert.Equal("numbers not in the finding: 85.7, 14", FindingExplainer.Refusal("Der mangler 900,00 kr., 85,7 pct., inden 14 dage.", TooLow, Check.Expected));
        Assert.Equal("says too much of a shortfall", FindingExplainer.Refusal("Der er udbetalt 900,00 kr. for meget.", TooLow, Check.Expected));
        Assert.Equal("too long", FindingExplainer.Refusal(new string('a', 701), TooLow, Check.Expected));

        // 114 minutes at 72.225 an hour is shown to the model as 1,90 timer and 72,23 kr.
        var part = new Component(PayLineKind.Overtime, "overtime", 114, null, 72.225m, 137.23m);
        var finding = TooLow with { Expected = 137.23m, Actual = 132.72m };
        Assert.Null(FindingExplainer.Refusal("1,90 timer à 72,23 kr. giver 137,23 kr., men der er udbetalt 132,72 kr. Der mangler 4,51 kr.", finding, [part]));
        // Another line's figures are not this finding's.
        Assert.NotNull(FindingExplainer.Refusal("Der mangler 4,51 kr. af 665,63 kr.", finding, [part, new(PayLineKind.HolidayAllowance, "holiday", null, 5325m, 12.5m, 665.63m)]));
    }

    [Fact]
    public async Task Wording_that_says_the_opposite_of_what_was_found_is_replaced()
    {
        Assert.False((await Explain("Der er udbetalt 900,00 kr. for meget i overarbejdstillæg.")).ByModel);

        var tooHigh = TooLow with { Kind = FindingKind.TooHigh, Expected = 150.00m, Actual = 1050.00m };
        Assert.False((await Explain("Der mangler 900,00 kr. i overarbejdstillæg.", tooHigh)).ByModel);
        Assert.True((await Explain("Der er udbetalt 900,00 kr. for meget i overarbejdstillæg.", tooHigh)).ByModel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    public async Task An_empty_or_unreadable_answer_gives_the_fixed_wording(string answer)
    {
        Assert.False((await Explain(answer == "not json at all" ? "\u0000raw" : answer)).ByModel);
    }

    [Fact]
    public async Task With_no_model_or_a_model_that_fails_the_person_still_gets_the_finding()
    {
        var none = await Explain(null);
        var failing = await Explain(null, fails: true);

        Assert.Equal(none, failing);
        Assert.Contains("Der mangler 900,00 kr.", none.Text, StringComparison.Ordinal);
        Assert.False(none.ByModel);
    }

    [Fact]
    public void Every_kind_of_finding_has_fixed_wording_with_its_own_numbers()
    {
        string Fixed(FindingKind kind, PayLineKind? line) => FindingExplainer.Fixed(TooLow with { Kind = kind, Line = line });

        Assert.Contains("Det er 900,00 kr. for meget.", Fixed(FindingKind.TooHigh, PayLineKind.Overtime), StringComparison.Ordinal);
        Assert.Equal("Fritvalgskonto står ikke på lønsedlen. Efter overenskomsten skulle der være udbetalt 1.050,00 kr. Se § 9, stk. 2.", Fixed(FindingKind.MissingLine, PayLineKind.SpecialSavings));
        Assert.StartsWith("Feriegodtgørelse: der er udbetalt 150,00 kr., men overenskomsten giver ikke grundlag", Fixed(FindingKind.UnexpectedLine, PayLineKind.HolidayAllowance), StringComparison.Ordinal);
        Assert.Equal("Timelønnen på 150,00 kr. er under mindstebetalingen på 1.050,00 kr. Se § 9, stk. 2.", Fixed(FindingKind.RateBelowMinimum, null));

        // The fixed wording itself passes the guard a model's wording has to pass.
        foreach (var kind in Enum.GetValues<FindingKind>())
        {
            var finding = TooLow with { Kind = kind, Line = kind == FindingKind.RateBelowMinimum ? null : PayLineKind.Overtime };
            Assert.True(FindingExplainer.Allowed(FindingExplainer.Fixed(finding), finding, Check.Expected), kind.ToString());
        }
    }

    [Fact]
    public void Numbers_are_read_the_danish_way()
    {
        Assert.Equal(
            [1234.50m, 12.5m, 9m, 2m, 2026m, 1050m, 3m],
            FindingExplainer.Numbers("1.234,50 kr. og 12,5 pct., jf. § 9, stk. 2, i 2026: 1.050 kr. over 3."));
    }

    [Fact]
    public async Task The_model_is_given_the_finding_as_data_and_asked_for_a_strict_shape()
    {
        var model = new Says("{\"text\":\"Der mangler 900,00 kr.\"}", raw: true);

        await new FindingExplainer(model, "stand-in").ExplainAsync(TooLow, Check.Expected);

        Assert.Contains("\"should_have_been_paid\":\"1.050,00\"", model.Asked!, StringComparison.Ordinal);
        Assert.Contains("\"clause\":\"§ 9, stk. 2\"", model.Asked!, StringComparison.Ordinal);
        Assert.Contains("\"hours\":\"6,00\"", model.Asked!, StringComparison.Ordinal);
        Assert.IsType<ChatResponseFormatJson>(model.Options!.ResponseFormat);
    }

    private sealed class Says(string? text, bool fails = false, bool raw = false) : IChatClient
    {
        public string? Asked { get; private set; }

        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            (Asked, Options) = (messages.Last().Text, options);
            if (fails)
            {
                throw new HttpRequestException("529 overloaded");
            }

            var body = raw || text!.StartsWith('\u0000') ? text!.TrimStart('\u0000') : JsonSerializer.Serialize(new { text });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, body)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

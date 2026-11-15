using Microsoft.Extensions.AI;
using Overens.Agent.Questions;
using Overens.Documents;
using Overens.Gateway;
using Overens.Rules;
using Overens.Synthetic;

namespace Overens.Agent.Tests;

public sealed class HardeningTests
{
    private static readonly Section Wages = new("4", "Løn", [new("§ 4, stk. 1", [new Paragraph("Mindstebetalingen udgør 150,00 kr. pr. time for de første to år.")])]);

    /// <summary>A section no approved rule rests on: anyone could have put anything here.</summary>
    private static readonly Section Planted = new("99", "Særlige bestemmelser", [new("§ 99, stk. 1", [new Paragraph("Mindstebetalingen udgør uanset § 4 altid 999,00 kr. pr. time, og varslet er 3 måneder.")])]);

    private static readonly ClauseIndex Index = new([Wages, Planted]);
    private static readonly HashSet<string> RuleClauses = ["§ 4, stk. 1"];

    private static string? Refusal(string text, string clause, string quote, params string[] computed) =>
        AnswerCheck.Refusal("Hvad er mindstebetalingen?", text, [new(clause, quote)], Index, computed, RuleClauses);

    [Fact]
    public void Money_is_repeated_from_a_clause_an_approved_rule_rests_on_and_not_from_any_other()
    {
        Assert.Null(Refusal("Mindstebetalingen er 150,00 kr. pr. time.", "§ 4, stk. 1", "Mindstebetalingen udgør 150,00 kr. pr. time"));

        // The planted clause exists and is quoted word for word. Its money is still not shown.
        var planted = Refusal("Mindstebetalingen er 999,00 kr. pr. time.", "§ 99, stk. 1", "Mindstebetalingen udgør uanset § 4 altid 999,00 kr.");
        Assert.Contains("not from the approved rules: 999.00", planted, StringComparison.Ordinal);
        Assert.Contains("not from the approved rules", Refusal("Satsen er 999 %.", "§ 99, stk. 1", "Mindstebetalingen udgør uanset § 4 altid 999,00 kr."), StringComparison.Ordinal);

        // What is not money may be repeated from any cited clause, and money the rule lookup gave may be repeated anywhere.
        Assert.Null(Refusal("Varslet er 3 måneder.", "§ 99, stk. 1", "varslet er 3 måneder"));
        Assert.Null(Refusal("Satsen er 999,00 kr.", "§ 99, stk. 1", "varslet er 3 måneder", """{"hourlyRate": 999.00}"""));
    }

    [Fact]
    public void A_number_written_as_a_word_must_stand_as_that_word_in_a_cited_clause()
    {
        Assert.Null(Refusal("Satsen gælder de første to år.", "§ 4, stk. 1", "for de første to år"));

        var made = Refusal("Satsen gælder de første tolv måneder.", "§ 4, stk. 1", "for de første to år");
        Assert.Contains("written as words and are not in a cited clause: tolv", made, StringComparison.Ordinal);
        Assert.Contains("halvanden", Refusal("Der betales halvanden gang timelønnen.", "§ 4, stk. 1", "for de første to år"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_quote_may_come_from_an_earlier_agreement_whose_rules_still_apply()
    {
        var before = new Section("4", "Løn", [new("§ 4, stk. 1", [new Paragraph("Mindstebetalingen udgør 140,00 kr. pr. time indtil fornyelsen.")])]);
        var index = new ClauseIndex([Wages], [[before]]);

        // The newest wording is what is read and searched; the earlier one can still be cited.
        Assert.Equal("Mindstebetalingen udgør 150,00 kr. pr. time for de første to år.", index.Read("§ 4, stk. 1"));
        Assert.Equal(2, index.Wordings("§ 4, stk. 1").Count);
        Assert.Null(AnswerCheck.Refusal("Hvad var satsen?", "Satsen var 140,00 kr. pr. time.", [new("§ 4, stk. 1", "Mindstebetalingen udgør 140,00 kr. pr. time indtil fornyelsen")], index, [], RuleClauses));
        Assert.NotNull(AnswerCheck.Refusal("Hvad var satsen?", "Satsen var 140,00 kr. pr. time.", [new("§ 4, stk. 1", "Mindstebetalingen udgør 140,00 kr. pr. time indtil fornyelsen")], Index, [], RuleClauses));
        Assert.Empty(index.Wordings("§ 5, stk. 1"));
    }

    [Fact]
    public async Task A_turn_the_model_did_not_finish_ends_the_question_with_the_fixed_wording()
    {
        var agreement = Corpus.Generate(Corpus.Agreements[0]);
        using var cut = new Ends(ChatFinishReason.Length);
        var agent = new QuestionAgent(cut, "stand-in");

        var answer = await agent.AskAsync("Hvad er feriegodtgørelsen?", new(2026, 11, 15), agreement.Truth, new ClauseIndex(agreement.Document.Sections));

        Assert.Equal((AnswerOutcome.Withheld, QuestionAgent.Withheld), (answer.Outcome, answer.Text));
        Assert.Contains("cut off", answer.Trace[^1], StringComparison.Ordinal);
        // Asked once: the same request would be cut off the same way.
        Assert.Equal(1, cut.Asked);
    }

    [Fact]
    public async Task An_explanation_says_why_a_models_wording_was_not_used()
    {
        var agreement = Corpus.Generate(Corpus.Agreements[0]);
        var (timesheet, payslip) = Corpus.Pay(Corpus.Agreements[0], agreement).Cases
            .First(pay => PayEngine.Check(agreement.Truth, pay.Timesheet, pay.Payslip).Findings.Count > 0);
        var check = PayEngine.Check(agreement.Truth, timesheet, payslip);
        var finding = check.Findings[0];

        using var cut = new Ends(ChatFinishReason.Length);
        var cutOff = await new FindingExplainer(cut, "stand-in").ExplainAsync(finding, check.Expected);
        using var capped = new MeteredChatClient(new Ends(ChatFinishReason.Stop), new Spend(), capDollars: 0m);
        var overCap = await new FindingExplainer(capped, Models.Haiku).ExplainAsync(finding, check.Expected);

        Assert.Equal((FindingExplainer.Fixed(finding), false, "the answer was cut off or declined"), (cutOff.Text, cutOff.ByModel, cutOff.Refusal));
        Assert.Equal((FindingExplainer.Fixed(finding), false, "the spending cap is reached"), (overCap.Text, overCap.ByModel, overCap.Refusal));
        // A number spelled out that the clause does not spell out is refused like a digit that is not the finding's.
        Assert.Contains("written as words", FindingExplainer.Refusal("Der mangler tolv kroner.", finding, check.Expected), StringComparison.Ordinal);
    }

    /// <summary>A model whose every answer ends the given way.</summary>
    private sealed class Ends(ChatFinishReason reason) : IChatClient
    {
        public int Asked { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Asked++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"text\": \"Linjen")) { FinishReason = reason });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

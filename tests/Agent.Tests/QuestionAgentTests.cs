using Microsoft.Extensions.AI;
using Overens.Agent.Questions;
using Overens.Documents;
using Overens.Rules;
using Overens.Synthetic;

namespace Overens.Agent.Tests;

public sealed class QuestionAgentTests
{
    private static readonly SyntheticAgreement Agreement = Corpus.Generate(Corpus.Agreements[2]);
    private static readonly ClauseIndex Index = new(Agreement.Document.Sections.Concat(Agreement.Document.Protocols));
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static readonly HolidayAllowanceRule Holiday = Agreement.Truth.Rules.OfType<HolidayAllowanceRule>().Single();

    private static FunctionCallContent Call(string name, object arguments) =>
        new(Guid.NewGuid().ToString("N"), name, System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(System.Text.Json.JsonSerializer.Serialize(arguments)));

    private static FunctionCallContent Answer(string text, params (string Clause, string Quote)[] citations) =>
        Call("give_answer", new { answered = true, text, citations = citations.Select(citation => new { clause_id = citation.Clause, quote = citation.Quote }) });

    private static async Task<(QuestionAnswer Answer, Scripted Model)> Ask(string question, params FunctionCallContent[] turns)
    {
        var model = new Scripted(turns);
        return (await new QuestionAgent(model, "stand-in").AskAsync(question, Today, Agreement.Truth, Index), model);
    }

    [Fact]
    public async Task An_answer_that_quotes_its_clause_and_uses_its_numbers_is_shown()
    {
        var (answer, model) = await Ask(
            "Hvad er feriegodtgørelsen?",
            Call("search_clauses", new { query = "feriegodtgørelse" }),
            Call("read_clause", new { clause_id = Holiday.Source.ClauseId }),
            Answer("Feriegodtgørelsen udgør 12,5 pct. af lønnen, jf. § 8, stk. 2.", (Holiday.Source.ClauseId, Holiday.Source.Quote)));

        Assert.Equal(AnswerOutcome.Answered, answer.Outcome);
        Assert.Equal("Feriegodtgørelsen udgør 12,5 pct. af lønnen, jf. § 8, stk. 2.", answer.Text);
        Assert.Equal(Holiday.Source.ClauseId, Assert.Single(answer.Citations).ClauseId);
        Assert.Equal(["search_clauses {\"query\":\"feriegodtgørelse\"}", $"read_clause {{\"clause_id\":\"{Holiday.Source.ClauseId}\"}}", "answered"], answer.Trace);

        // The model was given the search result and then the clause, as tool results.
        Assert.Contains(Holiday.Source.ClauseId, model.Results[0], StringComparison.Ordinal);
        Assert.Equal(Holiday.Source.Quote, model.Results[1]);
        Assert.All(model.Options, options => Assert.Equal(5, options.Tools!.Count));
    }

    [Fact]
    public async Task An_amount_must_come_from_the_calculator_and_a_sum_the_model_made_is_sent_back_once()
    {
        var shifts = new[] { new { date = "2026-11-09", start = "14:00", end = "23:30" } };
        var overtime = PayEngine.Expect(Agreement.Truth, new(new("x", new(2020, 1, 1)), new(2026, 11, 9), new(2026, 11, 9), [new(new(2026, 11, 9), new(14, 0), new(23, 30))]), 200m)
            .Components.Where(part => part.Line == PayLineKind.Overtime).Sum(part => part.Amount);
        var clause = Agreement.Truth.Rules.OfType<OvertimeRule>().Single().Source;
        var right = $"Overarbejdstillægget for vagten er {Danish.Money(overtime)} kr., jf. § 5, stk. 2.";

        var (answer, model) = await Ask(
            "En medarbejder med en timeløn på 200 kr. arbejder mandag den 9. november 2026 kl. 14.00-23.30. Hvad er overarbejdstillægget?",
            Call("calculate_pay", new { hourly_rate = 200, hired_on = "2020-01-01", shifts }),
            Answer($"Overarbejdstillægget er {Danish.Money(overtime + 100)} kr. inklusive et skøn.", (clause.ClauseId, clause.Quote)),
            Answer(right, (clause.ClauseId, clause.Quote)));

        Assert.Equal((AnswerOutcome.Answered, right), (answer.Outcome, answer.Text));
        Assert.StartsWith("refused: These numbers are not in a cited clause, in a tool result or in the question:", answer.Trace[1], StringComparison.Ordinal);
        Assert.Contains("The answer was not accepted", model.Results[1], StringComparison.Ordinal);

        // A second refusal ends it: the person gets fixed wording, not the model's.
        var (withheld, _) = await Ask(
            "Hvad er overarbejdstillægget for 2 timer ved 200 kr.?",
            Answer("Det er 150,00 kr.", (clause.ClauseId, clause.Quote)),
            Answer("Det er 151,00 kr.", (clause.ClauseId, clause.Quote)),
            Answer(right, (clause.ClauseId, clause.Quote)));
        Assert.Equal((AnswerOutcome.Withheld, QuestionAgent.Withheld), (withheld.Outcome, withheld.Text));
        Assert.Empty(withheld.Citations);
    }

    [Theory]
    [InlineData("Feriegodtgørelsen er på 12,5 pct. af lønnen.", "§ 8, stk. 2", "does not stand in that clause word for word")]
    [InlineData("Feriegodtgørelsen udgør 12,5 pct. af lønnen.", "§ 99, stk. 1", "has no clause")]
    [InlineData("", "", "cites no clause")]
    public async Task An_answer_with_a_reworded_quote_a_made_up_clause_or_no_citation_is_refused(string quote, string clause, string reason)
    {
        var bad = clause.Length == 0 ? Answer("Feriegodtgørelsen udgør 12,5 pct.") : Answer("Feriegodtgørelsen udgør 12,5 pct.", (clause, quote));

        var (answer, _) = await Ask("Hvad er feriegodtgørelsen?", bad, bad);

        Assert.Equal(AnswerOutcome.Withheld, answer.Outcome);
        Assert.All(answer.Trace, step => Assert.Contains(reason, step, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_number_from_a_clause_that_is_not_cited_is_not_the_answers_to_use()
    {
        var pension = Agreement.Truth.Rules.OfType<PensionRule>().First();
        var text = $"Feriegodtgørelsen udgør 12,5 pct., og arbejdsgiveren betaler {Danish.Percent(pension.EmployerPercent)} i pension.";

        var (uncited, _) = await Ask("Hvad er feriegodtgørelsen?", Answer(text, (Holiday.Source.ClauseId, Holiday.Source.Quote)), Answer(text, (Holiday.Source.ClauseId, Holiday.Source.Quote)));
        var (cited, _) = await Ask("Hvad er feriegodtgørelsen?", Answer(text, (Holiday.Source.ClauseId, Holiday.Source.Quote), (pension.Source.ClauseId, pension.Source.Quote)));

        Assert.Equal(AnswerOutcome.Withheld, uncited.Outcome);
        Assert.Equal(AnswerOutcome.Answered, cited.Outcome);
    }

    [Fact]
    public async Task What_the_agreement_does_not_say_is_answered_with_fixed_words_not_the_models()
    {
        var (answer, _) = await Ask(
            "Hvor mange ugers barselsorlov med løn har en far?",
            Call("search_clauses", new { query = "barselsorlov" }),
            Call("give_answer", new { answered = false, text = "Efter barselsloven har en far ret til 24 uger.", citations = Array.Empty<object>() }));

        Assert.Equal((AnswerOutcome.NotInAgreement, QuestionAgent.NotInAgreement), (answer.Outcome, answer.Text));
        Assert.DoesNotContain("24", answer.Text, StringComparison.Ordinal);

        // Said with nothing else in the call, it is taken the same way.
        var (bare, _) = await Ask("Har en medarbejder ret til firmabil?", Call("give_answer", new { answered = false }));
        Assert.Equal(AnswerOutcome.NotInAgreement, bare.Outcome);
        // An answer with parts missing is not: it is sent back.
        var (broken, _) = await Ask("Hvad er feriegodtgørelsen?", Call("give_answer", new { answered = true, text = "12,5 pct." }), Call("give_answer", new { text = "12,5 pct." }));
        Assert.Equal(AnswerOutcome.Withheld, broken.Outcome);
        Assert.All(broken.Trace, step => Assert.Equal("refused: The answer was not in the asked shape.", step));
    }

    [Fact]
    public async Task A_model_that_never_answers_is_stopped_and_one_that_stops_calling_tools_too()
    {
        var searching = Enumerable.Repeat(0, 30).Select(_ => Call("search_clauses", new { query = "pension" })).ToArray();

        var (endless, model) = await Ask("Hvad er pensionsbidraget?", searching);
        var (silent, _) = await Ask("Hvad er pensionsbidraget?");

        Assert.Equal(AnswerOutcome.Withheld, endless.Outcome);
        Assert.Equal(QuestionAgent.MaxTurns, model.Options.Count);
        // Text outside a tool call is never shown. The model is reminded once, then stopped.
        Assert.Equal((AnswerOutcome.Withheld, QuestionAgent.Withheld), (silent.Outcome, silent.Text));
        Assert.Equal(["no tool call", "no tool call", "gave no answer within the allowed turns"], silent.Trace);
    }

    [Fact]
    public async Task A_tool_call_that_cannot_be_carried_out_tells_the_model_so_and_does_not_end_the_conversation()
    {
        var (answer, model) = await Ask(
            "Hvad er mindstebetalingen?",
            Call("rules_in_force", new { date = "1. marts 2026" }),
            Call("read_clause", new { clause_id = "§ 77, stk. 9" }),
            Call("delete_everything", new { }),
            Call("calculate_pay", new { hourly_rate = 200, hired_on = "2020-01-01", shifts = Array.Empty<object>() }),
            Call("rules_in_force", new { date = "2026-03-01" }),
            Call("give_answer", new { answered = false, text = "", citations = Array.Empty<object>() }));

        Assert.Equal(AnswerOutcome.NotInAgreement, answer.Outcome);
        Assert.StartsWith("The call could not be carried out", model.Results[0], StringComparison.Ordinal);
        Assert.Equal("The agreement has no clause \"§ 77, stk. 9\".", model.Results[1]);
        Assert.Equal("There is no tool \"delete_everything\".", model.Results[2]);
        Assert.StartsWith("The call could not be carried out: Give between 1 and 62 shifts.", model.Results[3], StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"minimum_wage\"", model.Results[4], StringComparison.Ordinal);
    }

    [Fact]
    public void Clause_search_finds_the_clause_a_question_is_about()
    {
        string Top(string query) => Index.Search(query)[0].Id;

        Assert.Equal(Holiday.Source.ClauseId, Top("Hvad er feriegodtgørelsen?"));
        Assert.Equal(Agreement.Truth.Rules.OfType<OvertimeRule>().Single().Source.ClauseId, Top("tillæg for overarbejde de første timer"));
        Assert.StartsWith("Protokollat 1", Top("forhøjelse af pensionsbidrag protokollat"), StringComparison.Ordinal);
        // A joined Danish word is found by its first part.
        Assert.Contains(Index.Search("opsigelse"), hit => hit.Heading == "Opsigelse");
        Assert.Empty(Index.Search("xylofon"));
        Assert.Null(Index.Read("§ 99, stk. 1"));
    }

    /// <summary>A model that makes the tool calls it was given, one per turn, then stops calling tools.</summary>
    private sealed class Scripted(FunctionCallContent[] turns) : IChatClient
    {
        private int turn;

        public List<ChatOptions> Options { get; } = [];

        /// <summary>Each tool result the model was sent, in order.</summary>
        public List<string> Results { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options.Add(options!);
            if (messages.Last().Role == ChatRole.Tool)
            {
                Results.AddRange(messages.Last().Contents.OfType<FunctionResultContent>().Select(result => result.Result?.ToString() ?? ""));
            }

            var contents = turn < turns.Length ? new List<AIContent> { turns[turn++] } : [new TextContent("Jeg er færdig.")];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

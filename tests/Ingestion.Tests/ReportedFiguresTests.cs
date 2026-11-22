using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Overens.Documents;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Reading;
using Overens.Rules;

namespace Overens.Ingestion.Tests;

/// <summary>
/// Figures an agreement states only as a change on another figure. The model reports the
/// change as printed, the code makes the sum, and the check holds the change, not the sum,
/// to the agreement's text. Each test runs the real extractor on a few clauses written out
/// here, with a model that gives a fixed answer.
/// </summary>
public sealed class ReportedFiguresTests
{
    private const string WageQuote = "Mindstebetalingen pr. time udgør 134,20 kr.";
    private const string PensionQuote = "Det samlede pensionsbidrag udgør 13,2 pct. af lønnen.";
    private const string SavingsQuote = "Til fritvalgskontoen indbetaler arbejdsgiveren 6,4 pct. af lønnen.";

    private static readonly ReadAgreement Agreement = new(
        "Test",
        [
            new Section("§ 1", "Ikrafttræden", [new Clause("§ 1, stk. 1", [new Paragraph("Overenskomsten træder i kraft den 1. maj 2025.")])]),
            new Section("§ 4", "Løn",
            [
                new Clause("§ 4, stk. 1", [new Paragraph($"{WageQuote} Efter 6 måneders ansættelse forhøjes mindstebetalingen med 3,25 kr. pr. time. Efter 2 års ansættelse forhøjes den med yderligere 2,50 kr. pr. time.")]),
                new Clause("§ 4, stk. 2", [new Paragraph("Satserne forhøjes pr. 1. maj 2026 med 3,80 kr. pr. time.")]),
            ]),
            new Section("§ 14", "Pension",
            [
                new Clause("§ 14, stk. 1", [new Paragraph("Medarbejdere er omfattet af pensionsordningen efter seks måneders ansættelse.")]),
                new Clause("§ 14, stk. 2", [new Paragraph($"{PensionQuote} Arbejdsgiveren betaler to tredjedele af bidraget, og medarbejderen betaler resten.")]),
                new Clause("§ 14, stk. 3", [new Paragraph("Fra den første maj 2026 forhøjes arbejdsgiverens bidrag med 1,0 procentpoint, og medarbejderens bidrag nedsættes med 1,0 procentpoint.")]),
            ]),
            new Section("§ 16", "Fritvalg",
            [
                new Clause("§ 16, stk. 1", [new Paragraph($"{SavingsQuote} Pr. 1. maj 2026 forhøjes satsen med 1,0 procentpoint.")]),
            ]),
        ],
        new Dictionary<string, PageSpan>());

    private static JsonObject Item(string from, string? until, string clause, string quote) => new()
    {
        ["from"] = from,
        ["until"] = until,
        ["clause_id"] = clause,
        ["quote"] = quote,
        ["also_from"] = new JsonArray(),
    };

    private static JsonObject Band(int months, decimal? rate, decimal? step) =>
        new() { ["from_months"] = months, ["hourly_rate"] = rate, ["added_to_band_before"] = step };

    private static JsonObject Pension(string from, string? until, string clause, string quote, decimal? employer = null, decimal? employee = null,
        decimal? total = null, string? employerShare = null, decimal? employerPoints = null, decimal? employeePoints = null)
    {
        var item = Item(from, until, clause, quote);
        (item["employer_percent"], item["employee_percent"], item["employee_share"]) = (employer, employee, null);
        (item["total_percent"], item["employer_share"], item["employer_points"], item["employee_points"]) = (total, employerShare, employerPoints, employeePoints);
        item["qualifying_months"] = 6;
        return item;
    }

    private static JsonObject Wage(string from, string? until, string clause, string quote, decimal? increase, params JsonObject[] bands)
    {
        var item = Item(from, until, clause, quote);
        item["increase"] = increase;
        item["bands"] = new JsonArray([.. bands]);
        return item;
    }

    private static JsonObject Savings(string from, string? until, decimal? percent, decimal? points)
    {
        var item = Item(from, until, "§ 16, stk. 1", SavingsQuote);
        (item["percent"], item["points_added"]) = (percent, points);
        return item;
    }

    private static readonly JsonObject[] RightWage =
    [
        Wage("2025-05-01", "2026-05-01", "§ 4, stk. 1", WageQuote, null, Band(0, 134.20m, null), Band(6, null, 3.25m), Band(24, null, 2.50m)),
        Wage("2026-05-01", null, "§ 4, stk. 2", "Satserne forhøjes pr. 1. maj 2026 med 3,80 kr. pr. time.", 3.80m),
    ];

    private static readonly JsonObject[] RightPension =
    [
        Pension("2025-05-01", "2026-05-01", "§ 14, stk. 2", PensionQuote, total: 13.2m, employerShare: "two_thirds"),
        Pension("2026-05-01", null, "§ 14, stk. 3", "Fra den første maj 2026 forhøjes arbejdsgiverens bidrag med 1,0 procentpoint, og medarbejderens bidrag nedsættes med 1,0 procentpoint.", employerPoints: 1.0m, employeePoints: -1.0m),
    ];

    private static readonly JsonObject[] RightSavings = [Savings("2025-05-01", "2026-05-01", 6.4m, null), Savings("2026-05-01", null, null, 1.0m)];

    private static async Task<ExtractionResult> Extract(IEnumerable<JsonObject>? wage = null, IEnumerable<JsonObject>? pension = null, IEnumerable<JsonObject>? savings = null)
    {
        var answers = new Dictionary<string, IEnumerable<JsonObject>>
        {
            ["minimum_wage"] = wage ?? RightWage,
            ["pension"] = pension ?? RightPension,
            ["special_savings"] = savings ?? RightSavings,
        };
        return await new RuleExtractor(new FixedModel(answers), "fixed").ExtractAsync(Agreement);
    }

    private static List<string> Codes(ExtractionResult extraction, string id) =>
        [.. extraction.Rules.Single(rule => rule.Rule.Id == id).Problems.Select(problem => problem.Code)];

    [Fact]
    public async Task Steps_shares_of_a_total_and_changes_in_points_are_worked_out_by_code_and_pass_the_checks()
    {
        var extraction = await Extract();

        Assert.Empty(extraction.Unreadable);
        Assert.Empty(extraction.Rules.SelectMany(rule => rule.Problems));
        var wages = extraction.Rules.Select(rule => rule.Rule).OfType<MinimumWageRule>().ToList();
        Assert.Equal([134.20m, 137.45m, 139.95m], wages[0].Bands.Select(band => band.HourlyRate));
        Assert.Equal([138.00m, 141.25m, 143.75m], wages[1].Bands.Select(band => band.HourlyRate));
        var pensions = extraction.Rules.Select(rule => rule.Rule).OfType<PensionRule>().ToList();
        Assert.Equal([(8.8m, 4.4m), (9.8m, 3.4m)], pensions.Select(pension => (pension.EmployerPercent, pension.EmployeePercent)));
        Assert.Equal([6.4m, 7.4m], extraction.Rules.Select(rule => rule.Rule).OfType<SpecialSavingsRule>().Select(rule => rule.Percent));
    }

    [Fact]
    public async Task A_step_or_a_change_the_agreement_does_not_print_keeps_the_rule_out()
    {
        var extraction = await Extract(
            wage: [Wage("2025-05-01", "2026-05-01", "§ 4, stk. 1", WageQuote, null, Band(0, 134.20m, null), Band(6, null, 3.35m), Band(24, null, 2.50m)), RightWage[1]],
            pension: [Pension("2025-05-01", "2026-05-01", "§ 14, stk. 2", PensionQuote, total: 13.5m, employerShare: "two_thirds"), RightPension[1]],
            savings: [RightSavings[0], Savings("2026-05-01", null, null, 1.5m)]);

        Assert.Contains("value_not_in_cited_clauses", Codes(extraction, "minimum_wage-2025"));
        Assert.Contains("value_not_in_cited_clauses", Codes(extraction, "pension-2025"));
        Assert.Equal(["value_not_in_cited_clauses"], Codes(extraction, "special_savings-2026"));
        // The later periods are built on the flagged ones, and are flagged with them.
        Assert.Equal(["built_on_flagged_rule"], Codes(extraction, "minimum_wage-2026"));
        Assert.Equal(["built_on_flagged_rule"], Codes(extraction, "pension-2026"));
    }

    [Fact]
    public async Task A_share_of_the_total_in_words_the_agreement_does_not_use_keeps_the_rule_out()
    {
        var extraction = await Extract(pension: [Pension("2025-05-01", "2026-05-01", "§ 14, stk. 2", PensionQuote, total: 13.2m, employerShare: "three_quarters"), RightPension[1]]);

        Assert.Contains("value_not_in_cited_clauses", Codes(extraction, "pension-2025"));
    }

    [Fact]
    public async Task A_sum_the_model_made_itself_is_still_flagged()
    {
        // The rates after six months and two years, added up by the model. They are right, and printed nowhere.
        var extraction = await Extract(wage: [Wage("2025-05-01", "2026-05-01", "§ 4, stk. 1", WageQuote, null, Band(0, 134.20m, null), Band(6, 137.45m, null), Band(24, 139.95m, null)), RightWage[1]]);

        Assert.Equal(["value_not_in_cited_clauses", "value_not_in_cited_clauses"], Codes(extraction, "minimum_wage-2025"));
    }

    [Fact]
    public async Task A_change_with_no_period_before_or_a_step_on_no_band_cannot_be_read()
    {
        var extraction = await Extract(
            wage: [Wage("2025-05-01", null, "§ 4, stk. 1", WageQuote, null, Band(0, null, 3.25m))],
            savings: [Savings("2025-05-01", null, null, 1.0m)]);

        Assert.Equal(["minimum_wage", "special_savings"], extraction.Unreadable.Select(problem => problem.RuleId).Order());
        Assert.Contains(extraction.Gaps, gap => gap.RuleId == "minimum_wage" && gap.Code == "no_rule_of_kind");
    }

    [Fact]
    public async Task A_total_given_beside_both_shares_is_held_to_them()
    {
        var agreement = Agreement with
        {
            Sections =
            [
                .. Agreement.Sections.Where(section => section.Number != "§ 14"),
                new Section("§ 14", "Pension", [new Clause("§ 14, stk. 1", [new Paragraph("Pensionsbidraget udgør 12,5 pct. af lønnen, hvoraf arbejdsgiveren betaler 8,0 pct. og medarbejderen 4,5 pct. Ordningen gælder efter seks måneders ansættelse.")])]),
            ],
        };
        async Task<List<string>> Codes(decimal employee)
        {
            var pension = Pension("2025-05-01", null, "§ 14, stk. 1", "Pensionsbidraget udgør 12,5 pct. af lønnen, hvoraf arbejdsgiveren betaler 8,0 pct. og medarbejderen 4,5 pct.", employer: 8.0m, employee: employee, total: 12.5m);
            var extraction = await new RuleExtractor(new FixedModel(new() { ["pension"] = [pension] }), "fixed").ExtractAsync(agreement);
            Assert.Empty(extraction.Unreadable);
            return [.. extraction.Rules.Single().Problems.Select(problem => problem.Code)];
        }

        Assert.Empty(await Codes(4.5m));
        // 8,0 and 4,0 do not come to 12,5.
        Assert.Contains("shares_do_not_add_up", await Codes(4.0m));
    }

    /// <summary>Answers each kind it is asked for with the rules it was given, and the other kinds with none.</summary>
    private sealed class FixedModel(Dictionary<string, IEnumerable<JsonObject>> answers) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var kind = (options?.ResponseFormat as ChatResponseFormatJson)?.SchemaName ?? throw new InvalidOperationException("No schema.");
            var rules = new JsonArray([.. (answers.GetValueOrDefault(kind) ?? []).Select(rule => rule.DeepClone())]);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new JsonObject { ["rules"] = rules }.ToJsonString())));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

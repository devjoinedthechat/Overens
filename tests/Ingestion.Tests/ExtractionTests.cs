using System.Text.Json;
using System.Text.Json.Nodes;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Reading;
using Overens.Rules;
using Overens.Synthetic;

namespace Overens.Ingestion.Tests;

public sealed class ExtractionTests
{
    public static TheoryData<int, int> SeedsAndDifficulties
    {
        get
        {
            var data = new TheoryData<int, int>();
            for (var seed = 1; seed <= 21; seed++)
            {
                data.Add(seed, (seed % 3) + 1);
            }

            return data;
        }
    }

    private static async Task<(SyntheticAgreement Agreement, ExtractionResult Extraction, StandInModel Model)> Extract(
        int seed, int difficulty, Action<string, JsonArray>? spoil = null)
    {
        var agreement = AgreementGenerator.Generate(new($"extract-{seed}", seed, difficulty, 2026));
        var read = ClauseSegmenter.Segment(await new PdfLayoutReader().ReadAsync(AgreementPdf.Render(agreement.Document)));
        var model = new StandInModel(agreement.Document, agreement.Truth, spoil);
        return (agreement, await new RuleExtractor(model, "stand-in").ExtractAsync(read), model);
    }

    private static string Sorted(IEnumerable<Rule> rules) => RuleJson.Write(rules.OrderBy(rule => rule.Id, StringComparer.Ordinal));

    /// <summary>
    /// From PDF to checked rules, with a model that answers correctly: every true rule comes
    /// through, and the checks raise nothing against it.
    /// </summary>
    [Theory]
    [MemberData(nameof(SeedsAndDifficulties))]
    public async Task A_correct_answer_passes_every_check_and_gives_the_true_rules(int seed, int difficulty)
    {
        var (agreement, extraction, _) = await Extract(seed, difficulty);

        Assert.Empty(extraction.Unreadable);
        Assert.Empty(extraction.Rules.SelectMany(rule => rule.Problems));
        Assert.Equal(Sorted(agreement.Truth.Rules), Sorted(extraction.Sound));
    }

    [Fact]
    public async Task One_request_per_kind_carries_the_whole_agreement_and_a_strict_schema()
    {
        var (agreement, _, model) = await Extract(3, 3);

        Assert.Equal(ExtractionPrompt.Kinds.Select(kind => kind.Name).Order(), model.Requests.Select(request => request.Kind).Order());
        var ids = agreement.Document.Sections.Concat(agreement.Document.Protocols).SelectMany(section => section.Clauses).Select(clause => clause.Id);
        Assert.All(ids, id => Assert.Contains($"<clause id=\"{id}\">", model.Requests[0].Prompt, StringComparison.Ordinal));

        Assert.All(ExtractionPrompt.Kinds, kind =>
        {
            var rule = kind.Schema.GetProperty("properties").GetProperty("rules").GetProperty("items");
            Assert.False(rule.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(
                rule.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(),
                rule.GetProperty("required").EnumerateArray().Select(name => name.GetString()!).Order());
        });
    }

    [Fact]
    public async Task A_quote_that_is_reworded_keeps_the_rule_out()
    {
        var (_, extraction, _) = await Extract(4, 2, (kind, rules) =>
        {
            if (kind == "holiday_allowance")
            {
                rules[0]!["quote"] = "Feriegodtgørelsen er på 12,5 pct. af lønnen.";
            }
        });

        var holiday = Assert.Single(extraction.Rules, rule => rule.Rule is HolidayAllowanceRule);
        Assert.Equal(["quote_not_in_clause"], holiday.Problems.Select(problem => problem.Code));
        Assert.DoesNotContain(extraction.Sound, rule => rule is HolidayAllowanceRule);
    }

    [Fact]
    public async Task A_number_the_cited_clauses_do_not_print_keeps_the_rule_out()
    {
        var (_, extraction, _) = await Extract(4, 2, (kind, rules) =>
        {
            switch (kind)
            {
                case "pension":
                    // The total, read as the employer's share.
                    rules[0]!["employer_percent"] = rules[0]!["employer_percent"]!.GetValue<decimal>() + 0.3m;
                    break;
                case "minimum_wage":
                    rules[1]!["bands"]![0]!["hourly_rate"] = 1m + rules[1]!["bands"]![0]!["hourly_rate"]!.GetValue<decimal>();
                    break;
                case "overtime":
                    // The norm is in another section; without citing it, it is not grounded.
                    rules[0]!["also_from"] = new JsonArray();
                    break;
                default:
                    break;
            }
        });

        Assert.Equal(
            ["minimum_wage-2027", "overtime-2026", "pension-2026"],
            extraction.Rules.Where(rule => rule.Problems.Any(problem => problem.Code == "value_not_in_cited_clauses")).Select(rule => rule.Rule.Id).Order());
        Assert.Equal(extraction.Rules.Count - 3, extraction.Sound.Count());
    }

    [Fact]
    public async Task A_clause_or_a_date_the_agreement_does_not_have_keeps_the_rule_out()
    {
        var (_, extraction, _) = await Extract(4, 2, (kind, rules) =>
        {
            if (kind == "special_savings")
            {
                rules[0]!["clause_id"] = "§ 99, stk. 1";
                rules[2]!["from"] = "2028-04-01";
            }
        });

        var savings = extraction.Rules.Where(rule => rule.Rule is SpecialSavingsRule).ToList();
        Assert.Contains("unknown_clause", savings[0].Problems.Select(problem => problem.Code));
        Assert.Contains("date_not_in_agreement", savings[2].Problems.Select(problem => problem.Code));
        Assert.Empty(savings[1].Problems);
    }

    [Fact]
    public async Task Two_rules_of_a_kind_for_the_same_days_are_both_kept_out()
    {
        var (_, extraction, _) = await Extract(4, 2, (kind, rules) =>
        {
            if (kind == "holiday_allowance")
            {
                rules.Add(JsonNode.Parse(rules[0]!.ToJsonString()));
            }
        });

        var holiday = extraction.Rules.Where(rule => rule.Rule is HolidayAllowanceRule).ToList();
        Assert.Equal(["holiday_allowance-2026", "holiday_allowance-2026b"], holiday.Select(rule => rule.Rule.Id));
        Assert.Contains(holiday, rule => rule.Problems.Any(problem => problem.Code == "overlapping_period"));
    }

    [Fact]
    public async Task An_answer_that_cannot_be_read_is_reported_and_the_other_kinds_still_come_through()
    {
        var (agreement, extraction, _) = await Extract(4, 2, (kind, rules) =>
        {
            if (kind == "pension")
            {
                rules[0]!["from"] = "1. marts 2026";
            }
        });

        Assert.Equal(["pension"], extraction.Unreadable.Select(problem => problem.RuleId));
        Assert.Equal(agreement.Truth.Rules.Count - 1, extraction.Sound.Count());
    }
}

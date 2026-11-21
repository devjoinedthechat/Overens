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
        int seed, int difficulty, Action<string, JsonArray>? spoil = null, Twists twists = Twists.None)
    {
        var agreement = AgreementGenerator.Generate(new($"extract-{seed}", seed, difficulty, 2026, twists));
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
        Assert.Empty(extraction.Gaps);
        Assert.Empty(extraction.Rules.SelectMany(rule => rule.Problems));
        Assert.Equal(Sorted(agreement.Truth.Rules), Sorted(extraction.Sound));
    }

    [Fact]
    public async Task A_pension_share_given_as_half_comes_through_as_the_true_rule_with_nothing_flagged()
    {
        var (agreement, extraction, _) = await Extract(5, 3, twists: Twists.Fraction);

        Assert.Empty(extraction.Gaps);
        Assert.Empty(extraction.Rules.SelectMany(rule => rule.Problems));
        Assert.Equal(Sorted(agreement.Truth.Rules), Sorted(extraction.Sound));
        // Both periods: the main text, and the protocol that raises the employer's share and keeps the half.
        Assert.All(agreement.Truth.Rules.OfType<PensionRule>(), pension => Assert.Equal(pension.EmployerPercent / 2, pension.EmployeePercent));
    }

    [Fact]
    public async Task A_pension_with_no_employees_share_at_all_is_left_out_and_shows_as_a_gap()
    {
        var (_, extraction, _) = await Extract(5, 3, (kind, rules) =>
        {
            foreach (var rule in kind == "pension" ? rules : [])
            {
                (rule!["employee_percent"], rule["employee_share"]) = (null, null);
            }
        });

        Assert.Empty(extraction.Unreadable);
        Assert.DoesNotContain(extraction.Rules, rule => rule.Rule is PensionRule);
        Assert.Contains(extraction.Gaps, gap => gap.RuleId == "pension" && gap.Code == "no_rule_of_kind");
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

    /// <summary>Reports the later years as the agreement prints them: an increase, with the sentence that states it.</summary>
    private static Action<string, JsonArray> AsIncreases(SyntheticAgreement agreement, decimal off = 0) => (kind, rules) =>
    {
        if (kind != "minimum_wage")
        {
            return;
        }

        var lowest = rules.Select(rule => rule!["bands"]![0]!["hourly_rate"]!.GetValue<decimal>()).ToList();
        for (var year = 1; year < rules.Count; year++)
        {
            rules[year]!["increase"] = lowest[year] - lowest[year - 1] + off;
            rules[year]!["bands"] = new JsonArray();
            rules[year]!["quote"] = IncreaseSentence(agreement);
        }
    };

    private static string IncreaseSentence(SyntheticAgreement agreement) =>
        agreement.Document.Sections.SelectMany(section => section.Clauses).SelectMany(clause => clause.Blocks)
            .OfType<Overens.Documents.Paragraph>().Single(paragraph => paragraph.Text.StartsWith("Satserne forhøjes", StringComparison.Ordinal)).Text;

    [Fact]
    public async Task A_rate_given_as_an_increase_is_added_up_by_code_and_checked_by_the_increase()
    {
        var printed = AgreementGenerator.Generate(new("extract-5", 5, 3, 2026, Twists.Increases));
        var (agreement, extraction, _) = await Extract(5, 3, AsIncreases(printed), Twists.Increases);

        Assert.Empty(extraction.Rules.SelectMany(rule => rule.Problems));
        Assert.Equal(Sorted(agreement.Truth.Rules), Sorted(extraction.Sound.Select(rule =>
            rule is MinimumWageRule wage ? wage with { Source = agreement.Truth.Rules.Single(truth => truth.Id == wage.Id).Source } : rule)));

        // An increase the agreement does not print is caught, and so is every rate built on it.
        var (_, wrong, _) = await Extract(5, 3, AsIncreases(printed, off: 1), Twists.Increases);
        Assert.Equal(
            ["minimum_wage-2027", "minimum_wage-2028"],
            wrong.Rules.Where(rule => rule.Problems.Any(problem => problem.Code == "value_not_in_cited_clauses")).Select(rule => rule.Rule.Id).Order());

        // Sums the model made itself are not printed either, and are caught the same way.
        var (_, summed, _) = await Extract(5, 3, twists: Twists.Increases);
        Assert.Equal(2, summed.Rules.Count(rule => rule.Problems.Count > 0));
    }

    [Fact]
    public async Task A_rule_the_model_left_out_is_reported_as_a_gap()
    {
        var (_, extraction, _) = await Extract(4, 2, (kind, rules) =>
        {
            switch (kind)
            {
                case "minimum_wage":
                    // Only the first year, as a model does that will not add up an increase.
                    rules.RemoveAt(2);
                    rules.RemoveAt(1);
                    break;
                case "inconvenient_hours":
                    rules.RemoveAt(1);
                    break;
                case "pension":
                    rules.Clear();
                    break;
                default:
                    break;
            }
        });

        // Nothing is wrong with any rule that was proposed.
        Assert.Empty(extraction.Rules.SelectMany(rule => rule.Problems));
        Assert.Equal(
            [
                "inconvenient_hours: No rule of this kind applies from 2027-03-01 until 2028-03-01.",
                "minimum_wage: No rule of this kind applies from 2027-03-01.",
                "pension: No rule of this kind was proposed.",
            ],
            extraction.Gaps.Select(gap => $"{gap.RuleId}: {gap.Message}").Order());
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
                    // A share the clause does not print.
                    rules[0]!["employer_percent"] = rules[0]!["employer_percent"]!.GetValue<decimal>() + 0.3m;
                    break;
                case "minimum_wage":
                    rules[1]!["bands"]![0]!["hourly_rate"] = 1m + rules[1]!["bands"]![0]!["hourly_rate"]!.GetValue<decimal>();
                    break;
                case "overtime":
                    // The norm is in another section; without citing it, it is not grounded.
                    rules[0]!["also_from"] = new JsonArray();
                    break;
                case "special_savings":
                    // The other clauses of the quote's own section count as cited without being listed.
                    rules[0]!["also_from"] = new JsonArray();
                    break;
                case "inconvenient_hours":
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
    public async Task A_figure_from_another_years_column_keeps_the_rule_out()
    {
        var (_, extraction, _) = await Extract(4, 2, (kind, rules) =>
        {
            // Every figure is printed in the cited clause: under next year's date.
            if (kind == "minimum_wage")
            {
                rules[0]!["bands"] = rules[1]!["bands"]!.DeepClone();
            }
            else if (kind == "inconvenient_hours")
            {
                rules[1]!["windows"] = rules[2]!["windows"]!.DeepClone();
            }
        });

        Assert.Equal(
            ["inconvenient_hours-2027", "minimum_wage-2026"],
            extraction.Rules.Where(rule => rule.Problems.Count > 0).Select(rule => rule.Rule.Id).Order());
        Assert.All(extraction.Rules.SelectMany(rule => rule.Problems), problem => Assert.Equal("value_under_another_date", problem.Code));
    }

    [Fact]
    public async Task A_rate_from_another_bands_row_and_a_supplement_from_another_windows_row_keep_the_rule_out()
    {
        var (_, extraction, _) = await Extract(6, 3, (kind, rules) =>
        {
            if (kind == "minimum_wage")
            {
                var bands = rules[0]!["bands"]!.AsArray();
                var (lowest, highest) = (bands[0]!["hourly_rate"]!.GetValue<decimal>(), bands[^1]!["hourly_rate"]!.GetValue<decimal>());
                (bands[0]!["hourly_rate"], bands[^1]!["hourly_rate"]) = (highest, lowest);
            }
            else if (kind == "inconvenient_hours")
            {
                var windows = rules[0]!["windows"]!.AsArray();
                var (first, second) = (windows[0]!["amount_per_hour"]!.GetValue<decimal>(), windows[1]!["amount_per_hour"]!.GetValue<decimal>());
                (windows[0]!["amount_per_hour"], windows[1]!["amount_per_hour"]) = (second, first);
            }
        });

        Assert.Equal(
            ["inconvenient_hours-2026", "minimum_wage-2026"],
            extraction.Rules.Where(rule => rule.Problems.Count > 0).Select(rule => rule.Rule.Id).Order());
        Assert.All(extraction.Rules.SelectMany(rule => rule.Problems), problem => Assert.Equal("value_in_another_row", problem.Code));
    }

    [Fact]
    public async Task A_window_given_back_to_front_keeps_the_rule_out()
    {
        var (_, extraction, _) = await Extract(4, 2, (kind, rules) =>
        {
            if (kind == "inconvenient_hours")
            {
                // 22.00 to 18.00, where the row prints 18.00-22.00. Both times are printed.
                var window = rules[0]!["windows"]![0]!;
                (window["from"], window["to"]) = (window["to"]!.GetValue<string>(), window["from"]!.GetValue<string>());
            }
        });

        var flagged = Assert.Single(extraction.Rules, rule => rule.Problems.Count > 0);
        Assert.Equal("inconvenient_hours-2026", flagged.Rule.Id);
        Assert.Equal(["window_back_to_front"], flagged.Problems.Select(problem => problem.Code));
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
    public async Task Two_rules_of_a_kind_for_the_same_days_are_reported_as_a_conflict()
    {
        var (_, extraction, _) = await Extract(4, 2, (kind, rules) =>
        {
            if (kind == "holiday_allowance")
            {
                rules.Add(JsonNode.Parse(rules[0]!.ToJsonString()));
            }
        });

        // Neither rule is at fault, so neither is flagged. The conflict is reported for a person to settle.
        var holiday = extraction.Rules.Where(rule => rule.Rule is HolidayAllowanceRule).ToList();
        Assert.Equal(["holiday_allowance-2026", "holiday_allowance-2026b"], holiday.Select(rule => rule.Rule.Id));
        Assert.All(holiday, rule => Assert.Empty(rule.Problems));
        var conflict = Assert.Single(extraction.Gaps);
        Assert.Equal(("holiday_allowance", "conflicting_rules"), (conflict.RuleId, conflict.Code));
        Assert.Contains("holiday_allowance-2026 (from § 8, stk. 2) and holiday_allowance-2026b", conflict.Message, StringComparison.Ordinal);
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

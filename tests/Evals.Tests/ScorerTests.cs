using Overens.Ingestion.Extraction;
using Overens.Rules;
using Overens.Rules.Tests;
using Overens.Synthetic;

namespace Overens.Evals.Tests;

/// <summary>The scorer decides what counts as right. These tests decide whether the scorer is.</summary>
public sealed class ScorerTests
{
    private static readonly RuleSet Truth = Sample.Renewed;

    private static ExtractionResult Proposed(IEnumerable<Rule> rules, params string[] flagged) =>
        new([.. rules.Select(rule => new ProposedRule(rule, [], flagged.Contains(rule.Id) ? [new(rule.Id, "flagged", "")] : []))], [], []);

    private static KindScore Kind(IReadOnlyList<KindScore> scores, string kind) => scores.Single(score => score.Kind == kind);

    [Fact]
    public void The_true_rules_score_as_all_found_and_nothing_wrong()
    {
        var scores = ExtractionEval.Score(Truth, Proposed(Truth.Rules));

        Assert.All(scores, score => Assert.Equal((2, 2, 2, 0, 0, 0), (score.Expected, score.Proposed, score.Correct, score.Flagged, score.WrongAndUnflagged, score.RightButFlagged)));
    }

    [Fact]
    public void A_rule_is_the_same_rule_whatever_its_id_quote_or_way_of_writing_numbers()
    {
        var rewritten = Truth.Rules.Select(rule => rule switch
        {
            // 12.50 for 12.5, another id, another sentence quoted.
            HolidayAllowanceRule holiday => holiday with { Id = "x", Percent = 12.50m, Source = new("§ 99", "another sentence") },
            _ => rule,
        });

        Assert.All(ExtractionEval.Score(Truth, Proposed(rewritten)), score => Assert.Equal(score.Expected, score.Correct));
    }

    [Fact]
    public void Time_windows_are_compared_by_what_they_pay_not_by_how_they_are_listed()
    {
        var night = new TimeWindow([.. Enum.GetValues<DayOfWeek>()], new(22, 0), new(6, 0), 45.00m);
        var rule = new InconvenientHoursRule("a", new(Sample.Start), new("§ 1", "q"), [night]);
        var split = rule with
        {
            Windows =
            [
                night with { From = new(0, 0), To = new(6, 0) },
                night with { From = new(22, 0), To = new(0, 0), Days = [.. night.Days.Reverse()] },
            ],
        };

        Assert.Equal(ExtractionEval.Meaning(rule), ExtractionEval.Meaning(split));
        // An hour less, a krone more, a day missing, or another start date is another rule.
        Assert.NotEqual(ExtractionEval.Meaning(rule), ExtractionEval.Meaning(rule with { Windows = [night with { To = new(5, 0) }] }));
        Assert.NotEqual(ExtractionEval.Meaning(rule), ExtractionEval.Meaning(rule with { Windows = [night with { AmountPerHour = 46.00m }] }));
        Assert.NotEqual(ExtractionEval.Meaning(rule), ExtractionEval.Meaning(rule with { Windows = [night with { Days = [.. night.Days.Skip(1)] }] }));
        Assert.NotEqual(ExtractionEval.Meaning(rule), ExtractionEval.Meaning(rule with { InForce = new(Sample.Start.AddDays(1)) }));
    }

    [Fact]
    public void A_wrong_value_a_wrong_period_a_missing_rule_and_an_extra_rule_are_each_counted()
    {
        var pension = Truth.Rules.OfType<PensionRule>().ToList();
        var proposed = Truth.Rules
            .Where(rule => rule.Id != "savings-2")
            .Select(rule => rule switch
            {
                PensionRule { Id: "pension" } wrong => wrong with { EmployerPercent = 12m },
                HolidayAllowanceRule { Id: "holiday-2" } wrong => wrong with { InForce = new(new(2026, 4, 1)) },
                _ => rule,
            })
            .Append(new OvertimeRule("extra", new(new(2030, 1, 1)), new("§ 1", "q"), 7.5m, [new(null, 50)], 100));

        var scores = ExtractionEval.Score(Truth, Proposed(proposed, flagged: ["pension", "wage"]));

        // The wrong pension was flagged; the wrong holiday period was not.
        Assert.Equal((2, 2, 1, 1, 0, 0), Shape(Kind(scores, "pension")));
        Assert.Equal((2, 2, 1, 0, 1, 0), Shape(Kind(scores, "holiday_allowance")));
        Assert.Equal((2, 1, 1, 0, 0, 0), Shape(Kind(scores, "special_savings")));
        Assert.Equal((2, 3, 2, 0, 1, 0), Shape(Kind(scores, "overtime")));
        // A correct rule that was flagged is counted as needless work, not as an error.
        Assert.Equal((2, 2, 2, 1, 0, 1), Shape(Kind(scores, "minimum_wage")));
        Assert.Equal(2, pension.Count);

        static (int, int, int, int, int, int) Shape(KindScore score) =>
            (score.Expected, score.Proposed, score.Correct, score.Flagged, score.WrongAndUnflagged, score.RightButFlagged);
    }

    [Fact]
    public void The_same_true_rule_proposed_twice_is_found_once()
    {
        var twice = Truth.Rules.Concat(Truth.Rules.OfType<HolidayAllowanceRule>().Take(1).Select(rule => rule with { Id = "again" }));

        var holiday = Kind(ExtractionEval.Score(Truth, Proposed(twice)), "holiday_allowance");

        Assert.Equal((2, 3, 2), (holiday.Expected, holiday.Proposed, holiday.Correct));
    }

    [Fact]
    public void Payslips_are_scored_by_whether_the_findings_are_exactly_the_known_answers()
    {
        var pay = PayDataGenerator.Generate(Truth, seed: 7, new(2026, 3, 1), months: 4, employees: 12);
        var withErrors = pay.Errors.Count;

        var right = ExtractionEval.ScorePay(Proposed(Truth.Rules), pay);
        Assert.Equal((48, 48, 0, 0), (right.Cases, right.Right, right.MissedErrors, right.FalseAlarms));

        // Without the supplement rules every evening and night supplement looks unexpected,
        // and payslips that left one out look fine.
        var without = ExtractionEval.ScorePay(Proposed(Truth.Rules.Where(rule => rule is not InconvenientHoursRule)), pay);
        Assert.True(without.FalseAlarms > 0);
        Assert.True(without.MissedErrors > 0);
        Assert.Equal(48, without.Right + without.MissedErrors + without.FalseAlarms);
        Assert.InRange(without.MissedErrors, 1, withErrors);

        // Two correct payslips and one that left the savings out, checked without the savings rule:
        // the two correct ones are false alarms, and the wrong one looks fine, which is a missed error.
        var large = PayDataGenerator.Generate(Truth, seed: 7, new(2026, 3, 1), months: 12, employees: 12);
        var leftOut = large.Errors.First(error => error.Mistake == "special_savings_left_out");
        PayCase CaseOf(string employee, DateOnly month) => large.Cases.Single(pay => pay.Timesheet.Employee.Id == employee && pay.Timesheet.PeriodStart == month);
        var correct = large.Cases.Where(pay => !large.Errors.Any(error => error.EmployeeId == pay.Timesheet.Employee.Id && error.PeriodStart == pay.Timesheet.PeriodStart)).Take(2);
        var three = new PayData([.. correct, CaseOf(leftOut.EmployeeId, leftOut.PeriodStart)], [leftOut]);
        var noSavings = ExtractionEval.ScorePay(Proposed(Truth.Rules.Where(rule => rule is not SpecialSavingsRule)), three);
        Assert.Equal((3, 0, 1, 2), (noSavings.Cases, noSavings.Right, noSavings.MissedErrors, noSavings.FalseAlarms));

        // A flagged rule is not used: it is as if it had not been proposed.
        var flagged = ExtractionEval.ScorePay(Proposed(Truth.Rules, flagged: ["inconvenient", "inconvenient-2"]), pay);
        Assert.Equal(without, flagged);
    }
}

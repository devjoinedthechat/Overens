using System.Globalization;
using System.Text.RegularExpressions;
using Overens.Ingestion.Extraction;
using Overens.Rules;
using Overens.Synthetic;

namespace Overens.Evals;

/// <summary>How the rules proposed for one agreement compare with the rules it really states, for one kind of rule.</summary>
/// <param name="Kind">The kind of rule, as the extraction prompt names it.</param>
/// <param name="Expected">Rules the agreement states.</param>
/// <param name="Proposed">Rules the model proposed.</param>
/// <param name="Correct">True rules that some proposed rule states, in period and every value.</param>
/// <param name="Flagged">Proposed rules the model-free checks raised a problem on.</param>
/// <param name="WrongAndUnflagged">Wrong rules the checks let through: what a reviewer alone would have to catch.</param>
/// <param name="RightButFlagged">Correct rules the checks raised a problem on: needless work for a reviewer.</param>
public sealed record KindScore(string Kind, int Expected, int Proposed, int Correct, int Flagged, int WrongAndUnflagged, int RightButFlagged)
{
    public static KindScore operator +(KindScore left, KindScore right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new(
            left.Kind,
            left.Expected + right.Expected,
            left.Proposed + right.Proposed,
            left.Correct + right.Correct,
            left.Flagged + right.Flagged,
            left.WrongAndUnflagged + right.WrongAndUnflagged,
            left.RightButFlagged + right.RightButFlagged);
    }

    public static KindScore Add(KindScore left, KindScore right) => left + right;
}

/// <summary>
/// What the unflagged rules do when they check pay. Each case is one employee's month.
/// A case is right when the findings are exactly those the true rules give.
/// </summary>
/// <param name="Cases">Payslips checked.</param>
/// <param name="Right">Payslips whose findings were exactly right.</param>
/// <param name="MissedErrors">Payslips with a seeded error that was not reported in full.</param>
/// <param name="FalseAlarms">Correct payslips that were reported as wrong.</param>
public sealed record PayScore(int Cases, int Right, int MissedErrors, int FalseAlarms);

public static partial class ExtractionEval
{
    public static IReadOnlyList<KindScore> Score(RuleSet truth, ExtractionResult extraction)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(extraction);
        var scores = new List<KindScore>();
        foreach (var kind in ExtractionPrompt.Kinds.Select(kind => kind.Name))
        {
            var expected = truth.Rules.Where(rule => KindOf(rule) == kind).Select(Meaning).ToList();
            var proposed = extraction.Rules.Where(rule => KindOf(rule.Rule) == kind).ToList();
            var correct = proposed.Where(rule => expected.Contains(Meaning(rule.Rule))).ToList();
            scores.Add(new(
                kind,
                expected.Count,
                proposed.Count,
                correct.Select(rule => Meaning(rule.Rule)).Distinct().Count(),
                proposed.Count(rule => rule.Problems.Count > 0),
                proposed.Except(correct).Count(rule => rule.Problems.Count == 0),
                correct.Count(rule => rule.Problems.Count > 0)));
        }

        return scores;
    }

    /// <summary>
    /// Checks every pay case with the rules that passed the checks, as if a reviewer had
    /// approved exactly those, and compares the findings with the known answers.
    /// </summary>
    public static PayScore ScorePay(ExtractionResult extraction, PayData pay)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(pay);
        var rules = new RuleSet("extracted", 1, [.. extraction.Sound]);
        var (right, missed, falseAlarms) = (0, 0, 0);
        foreach (var (timesheet, payslip) in pay.Cases)
        {
            var answer = pay.Errors.SingleOrDefault(error => error.EmployeeId == timesheet.Employee.Id && error.PeriodStart == timesheet.PeriodStart);
            var expected = (answer?.Lines ?? []).Select(line => (line.Line, line.Expected, line.Actual)).ToHashSet();
            var expectedRate = answer?.RateBelowMinimum ?? false;

            var findings = PayEngine.Check(rules, timesheet, payslip).Findings;
            var found = findings.Where(finding => finding.Line is not null).Select(finding => (finding.Line!.Value, finding.Expected, finding.Actual)).ToHashSet();
            var foundRate = findings.Any(finding => finding.Kind == FindingKind.RateBelowMinimum);

            if (found.SetEquals(expected) && foundRate == expectedRate)
            {
                right++;
            }
            else if (answer is null)
            {
                falseAlarms++;
            }
            else
            {
                missed++;
            }
        }

        return new(pay.Cases.Count, right, missed, falseAlarms);
    }

    public static string KindOf(Rule rule) => rule switch
    {
        MinimumWageRule => "minimum_wage",
        OvertimeRule => "overtime",
        InconvenientHoursRule => "inconvenient_hours",
        PensionRule => "pension",
        HolidayAllowanceRule => "holiday_allowance",
        SpecialSavingsRule => "special_savings",
        _ => throw new NotSupportedException(),
    };

    /// <summary>
    /// What a rule says, without its id or its quote: two rules that pay the same on the same
    /// days are the same rule, whichever sentence was quoted and however it was written down.
    /// Time windows are compared by what they pay at each half hour of the week, so that
    /// 22-06 and 22-24 with 00-06 are equal.
    /// </summary>
    public static string Meaning(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule is InconvenientHoursRule hours)
        {
            var monday = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            var week = Enumerable.Range(0, 7 * 48).Select(slot => hours.AmountAt(monday.AddMinutes(slot * 30)).ToString("0.####", CultureInfo.InvariantCulture));
            return $"inconvenient_hours {hours.InForce} {string.Join(' ', week)}";
        }

        // 6.0 and 6 are the same number: trailing zeros are dropped before comparing.
        return TrailingZeros().Replace(RuleJson.Write(rule with { Id = "", Source = new("", "") }), "$1$2");
    }

    [GeneratedRegex(@"(\d)\.0+(?=\D)|(\.\d*?[1-9])0+(?=\D)")]
    private static partial Regex TrailingZeros();
}

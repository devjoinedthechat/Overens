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
/// <param name="Flagged">Proposed rules the model-free checks raised a problem on, or reported as in conflict with another.</param>
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
/// <param name="NotChecked">
/// Payslips with a line the true rules cover and the unflagged rules do not. The check says
/// so and decides nothing about that line, so the payslip is neither right nor wrong.
/// </param>
public sealed record PayScore(int Cases, int Right, int MissedErrors, int FalseAlarms, int NotChecked);

public static class ExtractionEval
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
            // A rule reported as in conflict with another has been put in front of a person, as a flagged rule has.
            bool Raised(ProposedRule rule) => rule.Problems.Count > 0
                || proposed.Any(other => !ReferenceEquals(other, rule) && other.Rule.InForce.Overlaps(rule.Rule.InForce));
            scores.Add(new(
                kind,
                expected.Count,
                proposed.Count,
                correct.Select(rule => Meaning(rule.Rule)).Distinct().Count(),
                proposed.Count(Raised),
                proposed.Except(correct).Count(rule => !Raised(rule)),
                correct.Count(rule => rule.Problems.Count > 0)));
        }

        return scores;
    }

    /// <summary>
    /// Checks every pay case with the rules that passed the checks, as if a reviewer had
    /// approved exactly those, and compares the findings with the known answers.
    /// </summary>
    public static PayScore ScorePay(RuleSet truth, ExtractionResult extraction, PayData pay)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(pay);
        // A rule in conflict with another is not approved until a person has chosen, so it checks no pay.
        var sound = extraction.Sound.ToList();
        var rules = new RuleSet("extracted", 1, [.. sound.Where(rule =>
            !sound.Any(other => !ReferenceEquals(other, rule) && other.GetType() == rule.GetType() && other.InForce.Overlaps(rule.InForce)))]);
        var (right, missed, falseAlarms, notChecked) = (0, 0, 0, 0);
        foreach (var (timesheet, payslip) in pay.Cases)
        {
            var answer = pay.Errors.SingleOrDefault(error => error.EmployeeId == timesheet.Employee.Id && error.PeriodStart == timesheet.PeriodStart);
            var expected = (answer?.Lines ?? []).Select(line => (line.Line, line.Expected, line.Actual)).ToHashSet();
            var expectedRate = answer?.RateBelowMinimum ?? false;

            var check = PayEngine.Check(rules, timesheet, payslip);
            if (check.Unchecked.Except(PayEngine.Check(truth, timesheet, payslip).Unchecked).Any())
            {
                notChecked++;
                continue;
            }

            var findings = check.Findings;
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

        return new(pay.Cases.Count, right, missed, falseAlarms, notChecked);
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

    /// <summary>What a rule says and the days it says it on, without its id or its quote. See <see cref="RuleMeaning"/>.</summary>
    public static string Meaning(Rule rule) => RuleMeaning.Of(rule);
}

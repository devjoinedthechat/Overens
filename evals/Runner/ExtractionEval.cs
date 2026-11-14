using System.Text.RegularExpressions;
using Overens.Ingestion.Extraction;
using Overens.Rules;

namespace Overens.Evals;

/// <summary>How one agreement's extraction compares with the rules the agreement really states.</summary>
/// <param name="Kind">The kind of rule, as the extraction prompt names it.</param>
/// <param name="Expected">Rules the agreement states.</param>
/// <param name="Proposed">Rules the model proposed.</param>
/// <param name="Correct">Proposed rules equal to a true rule in period and every value.</param>
/// <param name="Flagged">Proposed rules the model-free checks raised a problem on.</param>
/// <param name="WrongAndUnflagged">Wrong rules the checks let through: what a reviewer alone would have to catch.</param>
/// <param name="RightButFlagged">Correct rules the checks raised a problem on: needless work for a reviewer.</param>
public sealed record KindScore(string Kind, int Expected, int Proposed, int Correct, int Flagged, int WrongAndUnflagged, int RightButFlagged);

public static partial class ExtractionEval
{
    public static IReadOnlyList<KindScore> Score(RuleSet truth, ExtractionResult extraction)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(extraction);
        var scores = new List<KindScore>();
        foreach (var kind in ExtractionPrompt.Kinds.Select(kind => kind.Name))
        {
            var expected = truth.Rules.Where(rule => KindOf(rule) == kind).Select(Values).ToList();
            var proposed = extraction.Rules.Where(rule => KindOf(rule.Rule) == kind).ToList();
            var correct = proposed.Where(rule => expected.Contains(Values(rule.Rule))).ToList();
            scores.Add(new(
                kind,
                expected.Count,
                proposed.Count,
                correct.Select(rule => Values(rule.Rule)).Distinct().Count(),
                proposed.Count(rule => rule.Problems.Count > 0),
                proposed.Except(correct).Count(rule => rule.Problems.Count == 0),
                correct.Count(rule => rule.Problems.Count > 0)));
        }

        return scores;
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
    /// A rule's period and values, without its id or its quote: two rules that say the same
    /// thing are the same rule whichever sentence was quoted. Weekdays are put in order.
    /// </summary>
    private static string Values(Rule rule)
    {
        if (rule is InconvenientHoursRule hours)
        {
            rule = hours with { Windows = [.. hours.Windows.Select(window => window with { Days = [.. window.Days.Order()] }).OrderBy(window => window.From).ThenBy(window => window.AmountPerHour)] };
        }

        // 6.0 and 6 are the same number: trailing zeros are dropped before comparing.
        return TrailingZeros().Replace(RuleJson.Write(rule with { Id = "", Source = new("", "") }), "$1$2");
    }

    [GeneratedRegex(@"(\d)\.0+(?=\D)|(\.\d*?[1-9])0+(?=\D)")]
    private static partial Regex TrailingZeros();
}

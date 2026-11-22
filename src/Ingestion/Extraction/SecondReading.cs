using Overens.Rules;

namespace Overens.Ingestion.Extraction;

/// <summary>
/// A second model's reading of the same agreement, held against the first. A rule the
/// second reading does not state the same way, on some day the rule applies, is put to a
/// person.
///
/// The checks in <see cref="Grounding"/> hold each figure to the agreement's text. They
/// cannot see a rule made only of printed figures that are put together wrongly: a step
/// added to the wrong band, a change in points written as the percentage itself. Two
/// models reading independently seldom make the same such mistake, and where they do not,
/// they disagree. Where they make the same one, this does not help.
/// </summary>
public static class SecondReading
{
    public const string Code = "second_reading_differs";

    /// <summary>The problems to add to the first reading's rules, by rule id.</summary>
    public static IReadOnlyList<RuleProblem> Compare(IReadOnlyList<Rule> first, IReadOnlyList<Rule> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var problems = new List<RuleProblem>();
        foreach (var rule in first)
        {
            var others = second.Where(other => other.GetType() == rule.GetType()).ToList();
            // Every day on which either reading's rules of this kind change, within the days this rule applies.
            var days = others.SelectMany(other => new[] { other.InForce.From, other.InForce.Until }).OfType<DateOnly>()
                .Where(rule.InForce.Contains)
                .Append(rule.InForce.From)
                .Distinct()
                .Order();
            var says = RuleMeaning.Pays(rule);
            foreach (var day in days)
            {
                var there = others.Where(other => other.InForce.Contains(day)).ToList();
                if (there.Count == 0)
                {
                    problems.Add(new(rule.Id, Code, $"A second reading of the agreement has no rule of this kind on {day:yyyy-MM-dd}."));
                    break;
                }

                if (!there.Any(other => RuleMeaning.Pays(other) == says))
                {
                    problems.Add(new(rule.Id, Code, $"A second reading of the agreement gives other figures for {day:yyyy-MM-dd}."));
                    break;
                }
            }
        }

        return problems;
    }

    /// <summary>The first reading, with each rule the second reading differs on flagged.</summary>
    public static ExtractionResult Apply(ExtractionResult first, ExtractionResult second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var differs = Compare([.. first.Rules.Select(rule => rule.Rule)], [.. second.Rules.Select(rule => rule.Rule)]).ToLookup(problem => problem.RuleId);
        return first with
        {
            Rules = [.. first.Rules.Select(rule => differs[rule.Rule.Id].Any() ? rule with { Problems = [.. rule.Problems, .. differs[rule.Rule.Id]] } : rule)],
        };
    }
}

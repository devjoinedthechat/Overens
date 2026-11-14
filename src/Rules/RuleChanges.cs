namespace Overens.Rules;

/// <summary>
/// A span of days on which the rule of one kind would differ: what applies now, and what
/// would. Either may be null, when there is no rule of that kind on those days.
/// </summary>
public sealed record RuleChange(string Kind, DateOnly From, DateOnly? Until, Rule? Before, Rule? After);

/// <summary>
/// What publishing a rule set would change, day by day: which rates would differ, from when
/// to when. An approver sees this before approving, so a new version that quietly alters a
/// past period, or leaves a period without a rule, is in front of them.
/// </summary>
public static class RuleChanges
{
    private static readonly (Type Type, string Kind)[] Kinds =
    [
        (typeof(MinimumWageRule), "minimum_wage"),
        (typeof(OvertimeRule), "overtime"),
        (typeof(InconvenientHoursRule), "inconvenient_hours"),
        (typeof(PensionRule), "pension"),
        (typeof(HolidayAllowanceRule), "holiday_allowance"),
        (typeof(SpecialSavingsRule), "special_savings"),
    ];

    public static IReadOnlyList<RuleChange> Between(RuleSet? before, RuleSet after)
    {
        ArgumentNullException.ThrowIfNull(after);
        var changes = new List<RuleChange>();
        foreach (var (type, kind) in Kinds)
        {
            var was = (before?.Rules ?? []).Where(rule => rule.GetType() == type).ToList();
            var willBe = after.Rules.Where(rule => rule.GetType() == type).ToList();
            // Every day on which either set's rule of this kind starts or stops.
            var edges = was.Concat(willBe)
                .SelectMany(rule => new[] { rule.InForce.From, rule.InForce.Until })
                .OfType<DateOnly>().Distinct().Order().ToList();
            for (var index = 0; index < edges.Count; index++)
            {
                DateOnly? until = index + 1 < edges.Count ? edges[index + 1] : null;
                var (old, @new) = (was.SingleOrDefault(rule => rule.InForce.Contains(edges[index])), willBe.SingleOrDefault(rule => rule.InForce.Contains(edges[index])));
                if (Says(old) == Says(@new))
                {
                    continue;
                }

                // A change that carries on unaltered past an edge is one change, not two.
                if (changes is [.., { } last] && last.Kind == kind && last.Until == edges[index] && Says(last.Before) == Says(old) && Says(last.After) == Says(@new))
                {
                    changes[^1] = last with { Until = until };
                }
                else
                {
                    changes.Add(new(kind, edges[index], until, old, @new));
                }
            }
        }

        return changes;
    }

    /// <summary>What a rule says, apart from when it applies, what it is called and where it is quoted from.</summary>
    private static string Says(Rule? rule) =>
        rule is null ? "" : RuleJson.Write(rule with { Id = "", InForce = default, Source = new("", "") });
}

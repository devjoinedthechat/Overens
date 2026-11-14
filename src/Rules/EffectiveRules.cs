namespace Overens.Rules;

/// <summary>
/// The rules that apply now, from every published version of an agreement's rule set.
///
/// A version is never changed, so a later one takes over instead: from the day its first
/// rule of a kind starts, the earlier versions' rules of that kind no longer apply. A renewal
/// that starts in 2029 leaves the 2026 rules in force until then. A corrected upload that
/// starts on the same day replaces them altogether.
/// </summary>
public static class EffectiveRules
{
    public static RuleSet From(IEnumerable<RuleSet> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        var ordered = versions.OrderBy(version => version.Version).ToList();
        if (ordered.Count == 0)
        {
            throw new ArgumentException("An agreement with no published rule set has no rules.", nameof(versions));
        }

        var rules = new List<Rule>();
        for (var index = 0; index < ordered.Count; index++)
        {
            foreach (var rule in ordered[index].Rules)
            {
                // The first day a later version has a rule of this kind.
                var takenOver = ordered.Skip(index + 1)
                    .SelectMany(later => later.Rules)
                    .Where(later => later.GetType() == rule.GetType())
                    .Select(later => (DateOnly?)later.InForce.From)
                    .Min();
                if (takenOver is not { } end || (rule.InForce.Until is { } until && until <= end))
                {
                    rules.Add(rule);
                }
                else if (end > rule.InForce.From)
                {
                    rules.Add(rule with { InForce = rule.InForce with { Until = end } });
                }
            }
        }

        // Ids are unique within a version, not across them.
        return new(ordered[^1].AgreementId, ordered[^1].Version, [.. rules.Select(rule =>
            rules.Count(other => other.Id == rule.Id) > 1 ? rule with { Id = $"{rule.Id}@{rule.InForce.From:yyyyMMdd}" } : rule)]);
    }
}

namespace Overens.Rules;

/// <summary>
/// The rules of one version of one agreement. Once published a rule set is never changed;
/// a new version replaces it.
/// </summary>
public sealed record RuleSet(string AgreementId, int Version, IReadOnlyList<Rule> Rules)
{
    /// <summary>The rules that apply on a day: at most one of each kind.</summary>
    public RulesInForce InForce(DateOnly day) => new(
        Only<MinimumWageRule>(day),
        Only<OvertimeRule>(day),
        Only<InconvenientHoursRule>(day),
        Only<PensionRule>(day),
        Only<HolidayAllowanceRule>(day),
        Only<SpecialSavingsRule>(day));

    private TRule? Only<TRule>(DateOnly day)
        where TRule : Rule
    {
        TRule? found = null;
        foreach (var rule in Rules.OfType<TRule>())
        {
            if (!rule.InForce.Contains(day))
            {
                continue;
            }

            if (found is not null)
            {
                throw new InvalidOperationException(
                    $"Rules {found.Id} and {rule.Id} both apply on {day:yyyy-MM-dd}. Validate a rule set before using it.");
            }

            found = rule;
        }

        return found;
    }
}

/// <summary>The rules that apply on one day. A kind the agreement is silent on is null.</summary>
public sealed record RulesInForce(
    MinimumWageRule? MinimumWage,
    OvertimeRule? Overtime,
    InconvenientHoursRule? InconvenientHours,
    PensionRule? Pension,
    HolidayAllowanceRule? HolidayAllowance,
    SpecialSavingsRule? SpecialSavings);

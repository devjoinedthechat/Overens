namespace Overens.Rules;

/// <summary>Something wrong with a rule, found without reading the agreement.</summary>
public sealed record RuleProblem(string RuleId, string Code, string Message);

/// <summary>
/// Checks that a rule set is usable: values in range, and never two rules of a kind on the
/// same day. It cannot tell whether a rule matches the agreement; that is the reviewer's job.
/// </summary>
public static class RuleSetValidator
{
    public static IReadOnlyList<RuleProblem> Validate(RuleSet ruleSet)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        var problems = new List<RuleProblem>();

        foreach (var group in ruleSet.Rules.GroupBy(rule => rule.Id).Where(group => group.Count() > 1))
        {
            problems.Add(new(group.Key, "duplicate_id", "More than one rule has this id."));
        }

        foreach (var rule in ruleSet.Rules)
        {
            void Problem(string code, string message) => problems.Add(new(rule.Id, code, message));

            if (rule.InForce.Until is { } until && until <= rule.InForce.From)
            {
                Problem("empty_period", "The rule ends on or before the day it starts.");
            }

            if (string.IsNullOrWhiteSpace(rule.Source.ClauseId) || string.IsNullOrWhiteSpace(rule.Source.Quote))
            {
                Problem("no_source", "The rule does not say which clause it comes from, or does not quote it.");
            }

            switch (rule)
            {
                case MinimumWageRule wage:
                    Check(wage, Problem);
                    break;
                case OvertimeRule overtime:
                    Check(overtime, Problem);
                    break;
                case InconvenientHoursRule inconvenient:
                    Check(inconvenient, Problem);
                    break;
                case PensionRule pension:
                    if (!IsPercent(pension.EmployerPercent) || !IsPercent(pension.EmployeePercent))
                    {
                        Problem("percent_out_of_range", "A pension percentage is not between 0 and 100.");
                    }

                    if (pension.QualifyingMonths < 0)
                    {
                        Problem("negative_months", "The qualifying period is negative.");
                    }

                    break;
                case HolidayAllowanceRule { Percent: var percent } when !IsPercent(percent):
                case SpecialSavingsRule { Percent: var savings } when !IsPercent(savings):
                    Problem("percent_out_of_range", "The percentage is not between 0 and 100.");
                    break;
                default:
                    break;
            }
        }

        foreach (var kind in ruleSet.Rules.GroupBy(rule => rule.GetType()))
        {
            var rules = kind.OrderBy(rule => rule.InForce.From).ToList();
            for (var first = 0; first < rules.Count; first++)
            {
                for (var second = first + 1; second < rules.Count; second++)
                {
                    if (rules[first].InForce.Overlaps(rules[second].InForce))
                    {
                        problems.Add(new(
                            rules[second].Id,
                            "overlapping_period",
                            $"It applies on some of the same days as {rules[first].Id}, a rule of the same kind."));
                    }
                }
            }
        }

        return problems;
    }

    private static void Check(MinimumWageRule rule, Action<string, string> problem)
    {
        if (rule.Bands.Count == 0 || rule.Bands[0].FromMonths != 0)
        {
            problem("bands_do_not_start_at_zero", "The first seniority band must start at 0 months.");
        }

        if (rule.Bands.Zip(rule.Bands.Skip(1)).Any(pair => pair.Second.FromMonths <= pair.First.FromMonths))
        {
            problem("bands_out_of_order", "Seniority bands must start at increasing numbers of months.");
        }

        if (rule.Bands.Any(band => band.HourlyRate <= 0))
        {
            problem("rate_not_positive", "A minimum hourly rate is zero or negative.");
        }
    }

    private static void Check(OvertimeRule rule, Action<string, string> problem)
    {
        if (rule.DailyNormHours is <= 0 or > 24 || !IsWholeMinutes(rule.DailyNormHours))
        {
            problem("bad_daily_norm", "The daily norm must be a whole number of minutes, above 0 and at most 24 hours.");
        }

        if (rule.Tiers.Count == 0 || rule.Tiers[^1].Hours is not null)
        {
            problem("tiers_not_closed", "The last overtime tier must cover the rest of the day.");
        }

        if (rule.Tiers.SkipLast(1).Any(tier => tier.Hours is not > 0 || !IsWholeMinutes(tier.Hours.Value)))
        {
            problem("bad_tier_length", "Every overtime tier but the last needs a length in whole minutes.");
        }

        if (rule.Tiers.Any(tier => !IsSupplement(tier.Percent)) || !IsSupplement(rule.SundayPercent))
        {
            problem("percent_out_of_range", "An overtime supplement is not between 0 and 300 percent.");
        }
    }

    private static void Check(InconvenientHoursRule rule, Action<string, string> problem)
    {
        if (rule.Windows.Count == 0)
        {
            problem("no_windows", "The rule names no times.");
        }

        if (rule.Windows.Any(window => window.Days.Count == 0 || window.From == window.To))
        {
            problem("empty_window", "A time window has no days or no length.");
        }

        if (rule.Windows.Any(window => window.AmountPerHour <= 0))
        {
            problem("rate_not_positive", "A supplement is zero or negative.");
        }
    }

    private static bool IsPercent(decimal value) => value is >= 0 and <= 100;

    private static bool IsSupplement(decimal value) => value is >= 0 and <= 300;

    private static bool IsWholeMinutes(decimal hours) => hours * 60 == decimal.Truncate(hours * 60);
}

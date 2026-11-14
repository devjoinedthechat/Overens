using System.Globalization;
using System.Text.RegularExpressions;
using Overens.Documents;
using Overens.Ingestion.Reading;
using Overens.Rules;

namespace Overens.Ingestion.Extraction;

/// <summary>
/// Checks proposed rules against the agreement they claim to come from, with no model:
///
/// - the clause a rule names exists;
/// - its quote stands in that clause word for word;
/// - every number in the rule is printed in the section its quote is in or in another clause
///   it cites (for a rate given as an
///   increase, the increase is, and the sum is made by code);
/// - every date in the rule is printed in the agreement;
/// - the rules together are a usable rule set.
///
/// This cannot show that a rule is right. It shows that the rule's parts are the
/// agreement's own words and numbers, which is what a model that invents or misreads fails.
/// </summary>
public static partial class Grounding
{
    public static IReadOnlyList<ProposedRule> Check(ReadAgreement agreement, IReadOnlyList<(Rule Rule, IReadOnlyList<string> AlsoFrom, decimal? Increase)> rules)
    {
        ArgumentNullException.ThrowIfNull(agreement);
        ArgumentNullException.ThrowIfNull(rules);
        var clauses = agreement.Sections.SelectMany(section => section.Clauses)
            .ToDictionary(clause => clause.Id, clause => Flatten(ExtractionPrompt.Text(clause)));
        var everything = string.Join(' ', clauses.Values);
        var asASet = RuleSetValidator.Validate(new("proposed", 0, [.. rules.Select(rule => rule.Rule)]));

        var proposed = new List<ProposedRule>();
        foreach (var (rule, alsoFrom, increase) in rules)
        {
            var problems = new List<RuleProblem>();
            void Problem(string code, string message) => problems.Add(new(rule.Id, code, message));

            var cited = new List<string>();
            if (clauses.TryGetValue(rule.Source.ClauseId, out var clause))
            {
                cited.Add(clause);
                var quote = Flatten(rule.Source.Quote);
                if (quote.Length < 12 || !clause.Contains(quote, StringComparison.Ordinal))
                {
                    Problem("quote_not_in_clause", $"The quote does not stand word for word in {rule.Source.ClauseId}.");
                }
            }
            else
            {
                Problem("unknown_clause", $"The agreement has no clause {rule.Source.ClauseId}.");
            }

            foreach (var other in alsoFrom)
            {
                if (clauses.TryGetValue(other, out var text))
                {
                    cited.Add(text);
                }
                else
                {
                    Problem("unknown_clause", $"The agreement has no clause {other}.");
                }
            }

            // A rule's values are usually spread over the clauses of one section: the percentage in
            // stk. 1, the waiting period in stk. 2. The section the quote is in counts as cited,
            // whether or not the model listed its other clauses.
            if (agreement.Sections.FirstOrDefault(section => section.Clauses.Any(inSection => inSection.Id == rule.Source.ClauseId)) is { } home)
            {
                cited.AddRange(home.Clauses.Select(inSection => clauses[inSection.Id]));
            }

            var citedText = string.Join(' ', cited);
            // A rate that was added up here is not printed anywhere. What must be printed is the increase.
            var values = increase is { } added ? [($"The increase of {added}", Money(added))] : Values(rule);
            foreach (var (what, ways) in values)
            {
                if (!ways.Any(way => way.All(form => Printed(citedText, form))))
                {
                    Problem("value_not_in_cited_clauses", $"{what} is not printed in the clauses the rule cites.");
                }
            }

            foreach (var day in new[] { rule.InForce.From, rule.InForce.Until }.OfType<DateOnly>())
            {
                if (!everything.Contains(Danish.Date(day), StringComparison.Ordinal))
                {
                    Problem("date_not_in_agreement", $"The date {day:yyyy-MM-dd} is not printed in the agreement.");
                }
            }

            // Two rules of a kind for the same days are not a fault in either rule. They are a
            // conflict for a person to settle, and are reported with the gaps.
            problems.AddRange(asASet.Where(problem => problem.RuleId == rule.Id && problem.Code != "overlapping_period"));
            proposed.Add(new(rule, alsoFrom, problems));
        }

        return proposed;
    }

    /// <summary>
    /// What a set of proposed rules leaves for a person to settle: days no rule covers, and
    /// rules that cover the same days. Every kind of rule is expected to apply
    /// from the day the first rule of any kind starts, with no days missing, and to have a
    /// last rule with no end.
    /// </summary>
    public static IReadOnlyList<RuleProblem> Gaps(IReadOnlyList<Rule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var gaps = new List<RuleProblem>();
        if (rules.Count == 0)
        {
            return [.. KindNames.Values.Select(kind => new RuleProblem(kind, "no_rule_of_kind", "No rule of this kind was proposed."))];
        }

        var start = rules.Min(rule => rule.InForce.From);
        foreach (var (type, kind) in KindNames)
        {
            var ofKind = rules.Where(rule => rule.GetType() == type).OrderBy(rule => rule.InForce.From).ToList();
            for (var first = 0; first < ofKind.Count; first++)
            {
                foreach (var second in ofKind.Skip(first + 1).Where(second => ofKind[first].InForce.Overlaps(second.InForce)))
                {
                    gaps.Add(new(kind, "conflicting_rules",
                        $"{ofKind[first].Id} (from {ofKind[first].Source.ClauseId}) and {second.Id} (from {second.Source.ClauseId}) apply on the same days. Only one of them can be approved."));
                }
            }

            if (ofKind.Count == 0)
            {
                gaps.Add(new(kind, "no_rule_of_kind", "No rule of this kind was proposed."));
                continue;
            }

            DateOnly? coveredUntil = start;
            foreach (var rule in ofKind)
            {
                if (coveredUntil is { } until && rule.InForce.From > until)
                {
                    gaps.Add(new(kind, "period_not_covered", $"No rule of this kind applies from {until:yyyy-MM-dd} until {rule.InForce.From:yyyy-MM-dd}."));
                }

                coveredUntil = coveredUntil is null || rule.InForce.Until is null ? null
                    : rule.InForce.Until > coveredUntil ? rule.InForce.Until : coveredUntil;
            }

            if (coveredUntil is { } end)
            {
                gaps.Add(new(kind, "period_not_covered", $"No rule of this kind applies from {end:yyyy-MM-dd}."));
            }
        }

        return gaps;
    }

    private static readonly Dictionary<Type, string> KindNames = new()
    {
        [typeof(MinimumWageRule)] = "minimum_wage",
        [typeof(OvertimeRule)] = "overtime",
        [typeof(InconvenientHoursRule)] = "inconvenient_hours",
        [typeof(PensionRule)] = "pension",
        [typeof(HolidayAllowanceRule)] = "holiday_allowance",
        [typeof(SpecialSavingsRule)] = "special_savings",
    };

    /// <summary>
    /// Each number in a rule, with the ways an agreement may print it. A way is a set of
    /// forms that must all be there: 7.4 hours is printed as "7,4" or as "7" and "24".
    /// </summary>
    private static IEnumerable<(string What, string[][] Ways)> Values(Rule rule)
    {
        switch (rule)
        {
            case MinimumWageRule wage:
                foreach (var band in wage.Bands)
                {
                    yield return ($"The rate {band.HourlyRate}", Money(band.HourlyRate));
                    if (band.FromMonths > 0)
                    {
                        yield return ($"The seniority of {band.FromMonths} months", Months(band.FromMonths));
                    }
                }

                break;
            case OvertimeRule overtime:
                var norm = (int)(overtime.DailyNormHours * 60);
                yield return ($"The daily norm of {norm} minutes",
                    [[Number(norm / 60), Number(norm % 60)], [Decimal(overtime.DailyNormHours, "0.0")], [Decimal(overtime.DailyNormHours, "0.00")], [Number(norm)]]);
                foreach (var tier in overtime.Tiers)
                {
                    yield return ($"The supplement of {tier.Percent} percent", Percent(tier.Percent));
                    if (tier.Hours is { } hours)
                    {
                        yield return ($"The tier of {hours} hours", hours == decimal.Truncate(hours) ? Count((int)hours) : [[Number((int)(hours * 60))]]);
                    }
                }

                yield return ($"The Sunday supplement of {overtime.SundayPercent} percent", Percent(overtime.SundayPercent));
                break;
            case InconvenientHoursRule inconvenient:
                foreach (var window in inconvenient.Windows)
                {
                    yield return ($"The supplement {window.AmountPerHour}", Money(window.AmountPerHour));
                    yield return ($"The time {window.From:HH\\:mm}", Clock(window.From));
                    yield return ($"The time {window.To:HH\\:mm}", Clock(window.To));
                }

                break;
            case PensionRule pension:
                yield return ($"The employer's {pension.EmployerPercent} percent", Percent(pension.EmployerPercent));
                yield return ($"The employee's {pension.EmployeePercent} percent", Percent(pension.EmployeePercent));
                if (pension.QualifyingMonths > 0)
                {
                    yield return ($"The qualifying period of {pension.QualifyingMonths} months", Months(pension.QualifyingMonths));
                }

                break;
            case HolidayAllowanceRule holiday:
                yield return ($"The {holiday.Percent} percent", Percent(holiday.Percent));
                break;
            case SpecialSavingsRule savings:
                yield return ($"The {savings.Percent} percent", Percent(savings.Percent));
                break;
            default:
                break;
        }
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Decimal(decimal value, string format) => value.ToString(format, CultureInfo.InvariantCulture).Replace('.', ',');

    private static string[][] Money(decimal value) =>
        value == decimal.Truncate(value) ? [[Decimal(value, "0.00")], [Decimal(value, "0")]] : [[Decimal(value, "0.00")]];

    private static string[][] Percent(decimal value) =>
        value == decimal.Truncate(value) ? [[Decimal(value, "0.0")], [Decimal(value, "0")]] : [[Decimal(value, "0.0")], [Decimal(value, "0.0#")]];

    /// <summary>A small count, as digits or as a Danish word; one is also "første" and "et".</summary>
    private static string[][] Count(int value) =>
        value == 1 ? [["1"], ["en"], ["et"], ["første"]] : [[Number(value)], [Danish.Word(value)]];

    /// <summary>Months, or whole years where they divide: 12 months is "1 år".</summary>
    private static string[][] Months(int months) =>
        months % 12 == 0 ? [.. Count(months), .. Count(months / 12)] : Count(months);

    private static string[][] Clock(TimeOnly time) =>
        time == TimeOnly.MinValue
            ? [["24.00"], ["00.00"], ["24:00"], ["00:00"]]
            : [[time.ToString("HH\\.mm", CultureInfo.InvariantCulture)], [time.ToString("HH\\:mm", CultureInfo.InvariantCulture)]];

    /// <summary>Whether a form is printed as a whole number or word, not as part of a longer one.</summary>
    private static bool Printed(string text, string form) =>
        Regex.IsMatch(
            text,
            char.IsDigit(form[0])
                ? $@"(?<![\d]|\d[.,]){Regex.Escape(form)}(?!\d|[.,]\d)"
                : $@"(?<!\p{{L}}){Regex.Escape(form)}(?!\p{{L}})",
            RegexOptions.CultureInvariant);

    private static string Flatten(string text) => WhiteSpace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}

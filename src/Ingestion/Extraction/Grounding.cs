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
/// - every number in the rule is printed in the clauses it cites;
/// - every date in the rule is printed in the agreement;
/// - the rules together are a usable rule set.
///
/// This cannot show that a rule is right. It shows that the rule's parts are the
/// agreement's own words and numbers, which is what a model that invents or misreads fails.
/// </summary>
public static partial class Grounding
{
    public static IReadOnlyList<ProposedRule> Check(ReadAgreement agreement, IReadOnlyList<(Rule Rule, IReadOnlyList<string> AlsoFrom)> rules)
    {
        ArgumentNullException.ThrowIfNull(agreement);
        ArgumentNullException.ThrowIfNull(rules);
        var clauses = agreement.Sections.SelectMany(section => section.Clauses)
            .ToDictionary(clause => clause.Id, clause => Flatten(ExtractionPrompt.Text(clause)));
        var everything = string.Join(' ', clauses.Values);
        var asASet = RuleSetValidator.Validate(new("proposed", 0, [.. rules.Select(rule => rule.Rule)]));

        var proposed = new List<ProposedRule>();
        foreach (var (rule, alsoFrom) in rules)
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

            var citedText = string.Join(' ', cited);
            foreach (var (what, ways) in Values(rule))
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

            problems.AddRange(asASet.Where(problem => problem.RuleId == rule.Id));
            proposed.Add(new(rule, alsoFrom, problems));
        }

        return proposed;
    }

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

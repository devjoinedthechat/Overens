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
/// - a figure that stands in a table stands under the rule's date and in the rule's row
///   (see the other half of this class);
/// - a rate built on an earlier rule's rate is flagged when that rule is;
/// - every date in the rule is printed in the agreement;
/// - the rules together are a usable rule set.
///
/// This cannot show that a rule is right. It shows that the rule's parts are the
/// agreement's own words and numbers, which is what a model that invents or misreads fails.
/// </summary>
public static partial class Grounding
{
    /// <param name="agreement">The agreement as read.</param>
    /// <param name="rules">
    /// Each rule with the other clauses it draws on and, where the model reported something
    /// other than a final figure, what it reported (see <see cref="Reported"/>). Code made the
    /// figure; what must be printed is what the model reported.
    /// </param>
    public static IReadOnlyList<ProposedRule> Check(ReadAgreement agreement, IReadOnlyList<(Rule Rule, IReadOnlyList<string> AlsoFrom, Reported Reported)> rules)
    {
        ArgumentNullException.ThrowIfNull(agreement);
        ArgumentNullException.ThrowIfNull(rules);
        var clauses = agreement.Sections.SelectMany(section => section.Clauses)
            .ToDictionary(clause => clause.Id, clause => Flatten(ExtractionPrompt.Text(clause)));
        var byId = agreement.Sections.SelectMany(section => section.Clauses).ToDictionary(clause => clause.Id);
        var everything = Scan.Of(string.Join(' ', clauses.Values));
        var asASet = RuleSetValidator.Validate(new("proposed", 0, [.. rules.Select(rule => rule.Rule)]));

        var proposed = new List<ProposedRule>();
        foreach (var (rule, alsoFrom, reported) in rules)
        {
            var problems = new List<RuleProblem>();
            void Problem(string code, string message) => problems.Add(new(rule.Id, code, message));

            var cited = new List<string>();
            var citedClauses = new List<Clause>();
            if (clauses.TryGetValue(rule.Source.ClauseId, out var clause))
            {
                cited.Add(clause);
                citedClauses.Add(byId[rule.Source.ClauseId]);
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
                    citedClauses.Add(byId[other]);
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
                citedClauses.AddRange(home.Clauses);
            }

            // "§ 3" and "1. marts 2027" are not figures: a tier of three hours is not printed by a reference to § 3.
            var citedText = Scan.Of(string.Join(' ', cited));
            // A figure that was worked out here is not printed anywhere. What must be printed is what the model reported.
            var values = Values(rule, reported);
            if (rule is PensionRule shared && (reported.EmployeeShare is not null || reported.EmployerShare is not null)
                && (decimal.Round(shared.EmployeePercent, 2) != shared.EmployeePercent || decimal.Round(shared.EmployerPercent, 2) != shared.EmployerPercent))
            {
                // A third of 8,5 pct. is no percentage a payslip can be held to. A person has to say what the agreement means.
                Problem("share_not_exact", $"A pension share given as a part, {reported.EmployeeShare ?? reported.EmployerShare}, is not an exact percentage.");
            }

            // A total given beside both shares must be what they come to.
            if (rule is PensionRule { } added && reported is { TotalPercent: { } stated, EmployerShare: null } && added.EmployerPercent + added.EmployeePercent != stated)
            {
                Problem("shares_do_not_add_up", $"The shares, {added.EmployerPercent} and {added.EmployeePercent} percent, do not come to the total of {stated} percent.");
            }

            foreach (var (what, ways) in values)
            {
                if (!citedText.Prints(ways))
                {
                    Problem("value_not_in_cited_clauses", $"{what} is not printed in the clauses the rule cites.");
                }
            }

            CheckTables(rule, [.. citedClauses.Distinct()], reported, Problem);

            // A figure made from the period before is only as good as that period's figure.
            if (reported.BuildsOnEarlier && proposed.LastOrDefault(earlier => earlier.Rule.GetType() == rule.GetType()) is { Problems.Count: > 0 } flagged)
            {
                Problem("built_on_flagged_rule", $"A figure is that of {flagged.Rule.Id} with a change, and {flagged.Rule.Id} has a problem.");
            }

            foreach (var day in new[] { rule.InForce.From, rule.InForce.Until }.OfType<DateOnly>())
            {
                if (!everything.PrintsDay(day))
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
    private static IEnumerable<(string What, Need[][] Ways)> Values(Rule rule, Reported reported)
    {
        if (reported.Increase is { } increase)
        {
            yield return ($"The increase of {increase}", Money(increase));
            yield break;
        }

        switch (rule)
        {
            case MinimumWageRule wage:
                for (var index = 0; index < wage.Bands.Count; index++)
                {
                    var band = wage.Bands[index];
                    yield return reported.Steps?[index] is { } step
                        ? ($"The {step} added to the band before", Money(step))
                        : ($"The rate {band.HourlyRate}", Money(band.HourlyRate));
                    if (band.FromMonths > 0)
                    {
                        yield return ($"The seniority of {band.FromMonths} months", Months(band.FromMonths));
                    }
                }

                break;
            case OvertimeRule overtime:
                var norm = (int)(overtime.DailyNormHours * 60);
                // "7 timer og 24 minutter", "7,4", or "8 timer": whole hours print no minutes.
                yield return ($"The daily norm of {norm} minutes",
                    [.. norm % 60 == 0 ? Count(norm / 60) : [[Need.Whole(norm / 60), Need.Whole(norm % 60)]], [Need.Number(overtime.DailyNormHours)], [Need.Whole(norm)]]);
                var hoursBefore = 0m;
                foreach (var tier in overtime.Tiers)
                {
                    yield return ($"The supplement of {tier.Percent} percent", Percent(tier.Percent));
                    if (tier.Hours is { } hours)
                    {
                        Need[][] ways = hours == decimal.Truncate(hours) ? Count((int)hours) : [[Need.Whole((int)(hours * 60))]];
                        // "for første og anden overarbejdstime": a tier told by its first and last hour.
                        if (hours == decimal.Truncate(hours) && hoursBefore == decimal.Truncate(hoursBefore))
                        {
                            var (first, last) = ((int)hoursBefore + 1, (int)(hoursBefore + hours));
                            ways = [.. ways, hours == 1 ? [Need.Ordinal(first)] : [Need.Ordinal(first), Need.Ordinal(last)]];
                        }

                        yield return ($"The tier of {hours} hours", ways);
                        hoursBefore += hours;
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
                if (reported is { TotalPercent: { } total, EmployerShare: not null })
                {
                    // A total and the employer's part of it, in words. The employee pays the rest, which is not printed.
                    yield return ($"The total of {total} percent", Percent(total));
                    yield return ($"The employer's share, {reported.EmployerShare} of the total", Words(reported.EmployerShare!));
                }
                else
                {
                    yield return reported.EmployerPoints is { } employerPoints
                        ? ($"The change of {employerPoints} points to the employer's share", Percent(Math.Abs(employerPoints)))
                        : ($"The employer's {pension.EmployerPercent} percent", Percent(pension.EmployerPercent));
                    // A share the agreement gives as a part is printed as words, not as the percentage code made of it.
                    yield return reported.EmployeeShare is { } share ? ($"The employee's share, {share} of the employer's", Words(share))
                        : reported.EmployeePoints is { } employeePoints ? ($"The change of {employeePoints} points to the employee's share", Percent(Math.Abs(employeePoints)))
                        : ($"The employee's {pension.EmployeePercent} percent", Percent(pension.EmployeePercent));
                    if (reported.TotalPercent is { } printedTotal)
                    {
                        yield return ($"The total of {printedTotal} percent", Percent(printedTotal));
                    }
                }

                if (pension.QualifyingMonths > 0)
                {
                    yield return ($"The qualifying period of {pension.QualifyingMonths} months", Months(pension.QualifyingMonths));
                }

                break;
            case HolidayAllowanceRule holiday:
                yield return reported.Points is { } holidayPoints ? ($"The change of {holidayPoints} points", Percent(Math.Abs(holidayPoints))) : ($"The {holiday.Percent} percent", Percent(holiday.Percent));
                break;
            case SpecialSavingsRule savings:
                yield return reported.Points is { } savingsPoints ? ($"The change of {savingsPoints} points", Percent(Math.Abs(savingsPoints))) : ($"The {savings.Percent} percent", Percent(savings.Percent));
                break;
            default:
                break;
        }
    }

    /// <summary>A share in words, "halvdelen" or "to tredjedele": a phrase, not a figure.</summary>
    private static Need[][] Words(string share) => [.. Shares.Words(share).Select(words => new[] { Need.Phrase(words) })];

    private static Need[][] Money(decimal value) => [[Need.Number(value)]];

    private static Need[][] Percent(decimal value) => [[Need.Number(value)]];

    /// <summary>A small count, as a whole number or as a Danish word; one is also "første".</summary>
    private static Need[][] Count(int value) =>
        value == 1 ? [[Need.Whole(1)], [Need.Word(1)], [Need.Ordinal(1)]] : [[Need.Whole(value)], [Need.Word(value)]];

    /// <summary>Months, or whole years where they divide: 12 months is "1 år".</summary>
    private static Need[][] Months(int months) =>
        months % 12 == 0 ? [.. Count(months), .. Count(months / 12)] : Count(months);

    /// <summary>A time of day. Midnight is printed as "24.00" or "00.00", and both read as 00:00.</summary>
    private static Need[][] Clock(TimeOnly time) => [[Need.Clock(time)]];

    /// <summary>One thing that must be printed: a figure of a kind and value, or a phrase.</summary>
    /// <param name="WholeOnly">A count, which is printed with no decimals: "3 måneder", and not the 3 of "3,0 pct.".</param>
    private sealed record Need(FigureKind? Kind, decimal Value = 0, TimeOnly? Time = null, string? Words = null, bool WholeOnly = false)
    {
        /// <summary>An amount or a percentage, printed with or without decimals: 142, 142,00 and 142,0 are one amount.</summary>
        public static Need Number(decimal value) => new(FigureKind.Number, value);

        public static Need Whole(int value) => new(FigureKind.Number, value, WholeOnly: true);

        public bool MetBy(Figure figure) => figure.Kind == Kind
            && (Kind == FigureKind.Clock ? figure.Time == Time : figure.Value == Value)
            && !(WholeOnly && figure.Decimals);

        public static Need Word(int value) => new(FigureKind.Word, value);

        public static Need Ordinal(int value) => new(FigureKind.Ordinal, value);

        public static Need Clock(TimeOnly time) => new(FigureKind.Clock, Time: time);

        public static Need Phrase(string words) => new(null, Words: words);
    }

    /// <summary>A text and the figures read out of it, once.</summary>
    private sealed record Scan(string Raw, IReadOnlyList<Figure> Figures)
    {
        public static Scan Of(string text) => new(text, Documents.Figures.In(text));

        public bool Has(Need need) => need.Words is { } words ? PrintedWords(Raw, words) : Figures.Any(need.MetBy);

        /// <summary>Whether every need of one of the ways is printed.</summary>
        public bool Prints(Need[][] ways) => ways.Any(way => way.All(Has));

        public bool PrintsDay(DateOnly day) => Figures.Any(figure => figure.Kind == FigureKind.Date && figure.Day == day);

        /// <summary>Where the first figure a need is met by starts, and where the last one does.</summary>
        public (int? First, int? Last) Where(Need[][] ways)
        {
            var at = ways.SelectMany(way => way).Where(need => need.Kind is not null)
                .SelectMany(need => Figures.Where(need.MetBy))
                .Select(figure => figure.Start)
                .ToList();
            return at.Count == 0 ? (null, null) : (at.Min(), at.Max());
        }
    }

    /// <summary>Whether words are printed as words, not as part of longer ones: "halvdelen", "1/2".</summary>
    private static bool PrintedWords(string text, string words) =>
        Regex.IsMatch(
            text,
            char.IsDigit(words[0])
                ? $@"(?<![\d]|\d[.,]){Regex.Escape(words)}(?!\d|[.,]\d)"
                : $@"(?<!\p{{L}}){Regex.Escape(words)}(?!\p{{L}})",
            RegexOptions.CultureInvariant);

    private static string Flatten(string text) => WhiteSpace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

}

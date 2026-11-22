using System.Globalization;
using System.Text.RegularExpressions;
using Overens.Documents;
using Overens.Rules;

namespace Overens.Ingestion.Extraction;

/// <summary>
/// The checks that read tables. A figure printed in the cited clauses can still be the wrong
/// figure: next year's rate, or the rate of another seniority band. Where the agreement
/// itself says what a figure belongs to, that is checked here. In a table:
///
/// - under a heading that prints the day the rule starts, when the table has one;
/// - and in running text, not followed by "fra" and another day than the rule's;
/// - in the row of the rule's own seniority band or time window;
/// - with the window's times in the order the row prints them.
///
/// A figure that is also printed in running text is left alone, since the text may be where
/// the rule comes from. So is every figure in a clause without a table.
/// </summary>
public static partial class Grounding
{
    [GeneratedRegex(@"\b(?:måned|år|anciennitet)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeniorityWords();

    [GeneratedRegex(@"\b\d{1,2}[.:]\d{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ClockTime();

    private sealed record Cell(TableBlock Table, IReadOnlyList<string> Row, int Column);

    private static void CheckTables(Rule rule, IReadOnlyList<Clause> cited, Reported reported, Action<string, string> problem)
    {
        var tables = cited.SelectMany(clause => clause.Blocks).OfType<TableBlock>().Distinct().ToList();
        var sentences = Flatten(string.Join(' ', cited.SelectMany(clause => clause.Blocks).OfType<Paragraph>().Select(paragraph => paragraph.Text)));
        var prose = NotFigures().Replace(sentences, " ");
        var starts = rule.InForce.From;
        string[] dates = [.. DateForms(starts)];
        foreach (var (what, ways, _) in InTables(rule, reported))
        {
            // "5,0 pct. fra 1. marts 2026, 5,5 pct. fra 1. marts 2027": each figure is given its day in so many words.
            var given = ways.Where(way => way.Length == 1)
                .SelectMany(way => Regex.Matches(sentences, $@"(?<![\d.,]){Regex.Escape(way[0])}\s*(?:pct\.|procent|%|kr\.)?\s*(?:fra|pr\.)\s*(?:den\s*)?(?<day>\d{{1,2}}\.\s*\p{{L}}+\s+\d{{4}}|første\s+\p{{L}}+\s+\d{{4}}|\d{{1,2}}\.\d{{1,2}}\.\d{{4}})", RegexOptions.CultureInvariant))
                .Select(match => Flatten(match.Groups["day"].Value))
                .ToList();
            if (given.Count > 0 && !given.Any(day => dates.Contains(day, StringComparer.OrdinalIgnoreCase)))
            {
                problem("value_under_another_date", $"{what} is given from {given[0]}, and the rule starts on {dates[0]}.");
            }
        }

        if (tables.Count == 0)
        {
            return;
        }

        bool Dated(TableBlock table, int column) => column < table.Header.Count && dates.Any(date => PrintedDate(table.Header[column], date));
        var anyDated = tables.Any(table => Enumerable.Range(0, table.Header.Count).Any(column => Dated(table, column)));

        bool Labels(string row, string[][] ways) => ways.Any(way => way.All(form => Printed(row, form)));

        foreach (var (what, ways, label) in InTables(rule, reported))
        {
            bool Prints(string text) => ways.Any(way => way.All(form => Printed(text, form)));
            if (Prints(prose))
            {
                continue;
            }

            var found = tables
                .SelectMany(table => table.Rows.SelectMany(row => row.Select((text, column) => (Cell: new Cell(table, row, column), Text: text))))
                .Where(cell => Prints(NotFigures().Replace(cell.Text, " ")))
                .Select(cell => cell.Cell)
                .ToList();
            if (found.Count == 0)
            {
                continue;
            }

            if (anyDated)
            {
                found = [.. found.Where(cell => Dated(cell.Table, cell.Column))];
                if (found.Count == 0)
                {
                    problem("value_under_another_date", $"{what} stands in a table, and not under {dates[0]}, the day the rule starts.");
                    continue;
                }
            }

            // What a row is about is printed in the cells before the figure: "Mandag-fredag | kl. 15.00-23.00 | 19,60 kr.".
            // A grade's number, "Løntrin 2", is not a seniority.
            static string Label(IReadOnlyList<string> row, int column) => NotFigures().Replace(string.Join(" ", row.Take(column)), " ");
            var rows = found.Where(cell => cell.Column > 0).Select(cell => Label(cell.Row, cell.Column)).ToList();
            if (label is null || rows.Count == 0)
            {
                continue;
            }

            // A table whose rows are not told apart by seniority, or by time of day, says nothing about which row a
            // figure is in: "Løntrin 2" is a grade, and what it takes to reach it is stated elsewhere.
            var labelled = found.Where(cell => cell.Column > 0).Any(cell => cell.Table.Rows.Any(row =>
                (label.Value.To is null ? SeniorityWords() : ClockTime()).IsMatch(Label(row, cell.Column))));
            if (!labelled)
            {
                continue;
            }

            if (label.Value.To is not { } until)
            {
                if (!rows.Any(row => Labels(row, label.Value.From)))
                {
                    problem("value_in_another_row", $"{what} stands in a table row that is not the row for {label.Value.Name}.");
                }

                continue;
            }

            int? At(string row, string[][] times) => times.SelectMany(way => way).Where(form => Printed(row, form)).Select(form => (int?)row.IndexOf(form, StringComparison.Ordinal)).Min();
            // A row may hold two windows, "lørdag kl. 14.00-24.00 og søndag kl. 05.00-24.00": the end is looked for after the start.
            int? Last(string row, string[][] times) => times.SelectMany(way => way).Where(form => Printed(row, form)).Select(form => (int?)row.LastIndexOf(form, StringComparison.Ordinal)).Max();
            if (!rows.Any(row => At(row, label.Value.From) is not null) || !rows.Any(row => At(row, until) is not null))
            {
                problem("value_in_another_row", $"{what} stands in a table row that is not the row for {label.Value.Name}.");
            }
            else if (rows.Where(row => At(row, label.Value.From) is not null && At(row, until) is not null) is var both && both.Any() && both.All(row => At(row, label.Value.From) > Last(row, until)))
            {
                problem("window_back_to_front", $"The row for {what} prints the times the other way round from {label.Value.Name}.");
            }
        }
    }

    /// <summary>
    /// The figures of a rule that a table can place, each with what its row should say:
    /// a seniority, or a window's two times.
    /// </summary>
    private static IEnumerable<(string What, string[][] Ways, (string Name, string[][] From, string[][]? To)? Label)> InTables(Rule rule, Reported reported)
    {
        if (reported.Increase is { } added)
        {
            yield return ($"The increase of {added}", Money(added), null);
            yield break;
        }

        // A figure made from a share or a change in points is not printed. What was reported is checked elsewhere.
        if (reported.EmployerShare is not null || reported.EmployerPoints is not null || reported.EmployeePoints is not null || reported.Points is not null)
        {
            yield break;
        }

        switch (rule)
        {
            case MinimumWageRule wage:
                for (var index = 0; index < wage.Bands.Count; index++)
                {
                    var band = wage.Bands[index];
                    // A step added to the band before stands in running text, with nothing in a row to hold it to.
                    yield return reported.Steps?[index] is { } step
                        ? ($"The {step} added to the band before", Money(step), null)
                        : ($"The rate {band.HourlyRate}", Money(band.HourlyRate),
                            band.FromMonths > 0 ? ($"{band.FromMonths} months of seniority", Months(band.FromMonths), null) : null);
                }

                break;
            case InconvenientHoursRule inconvenient:
                foreach (var window in inconvenient.Windows)
                {
                    yield return ($"The supplement {window.AmountPerHour}", Money(window.AmountPerHour),
                        ($"{window.From:HH\\:mm} to {window.To:HH\\:mm}", Clock(window.From), Clock(window.To)));
                }

                break;
            case OvertimeRule overtime:
                foreach (var percent in overtime.Tiers.Select(tier => tier.Percent).Append(overtime.SundayPercent).Distinct())
                {
                    yield return ($"The supplement of {percent} percent", Percent(percent), null);
                }

                break;
            case PensionRule pension:
                yield return ($"The employer's {pension.EmployerPercent} percent", Percent(pension.EmployerPercent), null);
                break;
            case HolidayAllowanceRule holiday:
                yield return ($"The {holiday.Percent} percent", Percent(holiday.Percent), null);
                break;
            case SpecialSavingsRule savings:
                yield return ($"The {savings.Percent} percent", Percent(savings.Percent), null);
                break;
            default:
                break;
        }
    }
}

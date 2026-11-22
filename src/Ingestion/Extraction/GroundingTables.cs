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

    /// <summary>What may stand between a figure and the day it is given from: "5,0 pct. fra 1. marts", "38,40 kr. pr. 1.3.2027".</summary>
    [GeneratedRegex(@"^\s*(?:pct\.|procent|%|kr\.)?\s*(?:fra|pr\.)\s*(?:den\s*)?", RegexOptions.CultureInvariant)]
    private static partial Regex GivenFrom();

    private sealed record Cell(TableBlock Table, IReadOnlyList<string> Row, int Column);

    private static void CheckTables(Rule rule, IReadOnlyList<Clause> cited, Reported reported, Action<string, string> problem)
    {
        var tables = cited.SelectMany(clause => clause.Blocks).OfType<TableBlock>().Distinct().ToList();
        var prose = Scan.Of(Flatten(string.Join(' ', cited.SelectMany(clause => clause.Blocks).OfType<Paragraph>().Select(paragraph => paragraph.Text))));
        var starts = rule.InForce.From;
        foreach (var (what, ways, _) in InTables(rule, reported))
        {
            // "5,0 pct. fra 1. marts 2026, 5,5 pct. fra 1. marts 2027": each figure is given its day in so many words.
            var given = (
                from need in ways.Where(way => way is [{ Kind: FigureKind.Number }]).Select(way => way[0])
                from figure in prose.Figures.Where(figure => figure.Kind == FigureKind.Number && figure.Value == need.Value)
                let between = GivenFrom().Match(prose.Raw[figure.End..])
                where between.Success
                let date = prose.Figures.FirstOrDefault(other => other.Kind == FigureKind.Date && other.Start == figure.End + between.Length)
                where date?.Day is not null
                select date).ToList();
            if (given.Count > 0 && !given.Any(date => date.Day == starts))
            {
                problem("value_under_another_date", $"{what} is given from {prose.Raw[given[0].Start..given[0].End]}, and the rule starts on {Danish.Date(starts)}.");
            }
        }

        if (tables.Count == 0)
        {
            return;
        }

        bool Dated(TableBlock table, int column) => column < table.Header.Count && Scan.Of(table.Header[column]).PrintsDay(starts);
        var anyDated = tables.Any(table => Enumerable.Range(0, table.Header.Count).Any(column => Dated(table, column)));

        foreach (var (what, ways, label) in InTables(rule, reported))
        {
            if (prose.Prints(ways))
            {
                continue;
            }

            var found = tables
                .SelectMany(table => table.Rows.SelectMany(row => row.Select((text, column) => (Cell: new Cell(table, row, column), Text: text))))
                .Where(cell => Scan.Of(cell.Text).Prints(ways))
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
                    problem("value_under_another_date", $"{what} stands in a table, and not under {Danish.Date(starts)}, the day the rule starts.");
                    continue;
                }
            }

            // What a row is about is printed in the cells before the figure: "Mandag-fredag | kl. 15.00-23.00 | 19,60 kr.".
            // A grade's number, "Løntrin 2", is a reference, not a seniority.
            static Scan Label(IReadOnlyList<string> row, int column) => Scan.Of(string.Join(" ", row.Take(column)));
            var rows = found.Where(cell => cell.Column > 0).Select(cell => Label(cell.Row, cell.Column)).ToList();
            if (label is null || rows.Count == 0)
            {
                continue;
            }

            // A table whose rows are not told apart by seniority, or by time of day, says nothing about which row a
            // figure is in: "Løntrin 2" is a grade, and what it takes to reach it is stated elsewhere.
            var labelled = found.Where(cell => cell.Column > 0).Any(cell => cell.Table.Rows.Any(row => Label(row, cell.Column) is var each
                && (label.Value.To is null ? SeniorityWords().IsMatch(each.Raw) : each.Figures.Any(figure => figure.Kind == FigureKind.Clock))));
            if (!labelled)
            {
                continue;
            }

            if (label.Value.To is not { } until)
            {
                if (!rows.Any(row => row.Prints(label.Value.From)))
                {
                    problem("value_in_another_row", $"{what} stands in a table row that is not the row for {label.Value.Name}.");
                }

                continue;
            }

            // A row may hold two windows, "lørdag kl. 14.00-24.00 og søndag kl. 05.00-24.00": the end is looked for after the start.
            if (!rows.Any(row => row.Where(label.Value.From).First is not null) || !rows.Any(row => row.Where(until).First is not null))
            {
                problem("value_in_another_row", $"{what} stands in a table row that is not the row for {label.Value.Name}.");
            }
            else if (rows.Where(row => row.Where(label.Value.From).First is not null && row.Where(until).First is not null) is var both && both.Any()
                && both.All(row => row.Where(label.Value.From).First > row.Where(until).Last))
            {
                problem("window_back_to_front", $"The row for {what} prints the times the other way round from {label.Value.Name}.");
            }
        }
    }

    /// <summary>
    /// The figures of a rule that a table can place, each with what its row should say:
    /// a seniority, or a window's two times.
    /// </summary>
    private static IEnumerable<(string What, Need[][] Ways, (string Name, Need[][] From, Need[][]? To)? Label)> InTables(Rule rule, Reported reported)
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

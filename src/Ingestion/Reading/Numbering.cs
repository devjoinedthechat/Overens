using System.Text.RegularExpressions;

namespace Overens.Ingestion.Reading;

/// <summary>
/// Agreements number their clauses in more than one way. The splitter knows one: a section
/// headed "§ 5. Overarbejde" with clauses that begin "Stk. 2.". This puts a second way into
/// that form before the splitter sees it:
///
///   § 1 ARBEJDSTID            a section, in bold capitals, with no full stop
///   5. Forskudttidstillæg     a clause, as a bold numbered heading; the agreement itself
///                             refers to it as "§ 1, stk. 5"
///
/// and after the last § section, the agreements appended to the main one, each under a bold
/// heading in capitals. Those have no number of their own, so they are numbered here in the
/// order they stand: "Aftale 3", with the printed heading kept as the section's heading.
///
/// It is told which way an agreement is written by whether any line begins "Stk. 1.".
/// </summary>
public static partial class Numbering
{
    public static IReadOnlyList<LayoutPage> Normalise(IReadOnlyList<LayoutPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var lines = pages.SelectMany(page => page.Items.OfType<LayoutLine>()).ToList();
        if (lines.Any(line => StkClause().IsMatch(line.Text)) || !lines.Any(line => line.Bold && Section().IsMatch(line.Text)))
        {
            return pages;
        }

        // Headings are set larger than the body, whose size is the one most lines share.
        var body = lines.GroupBy(line => line.FontSize).MaxBy(sizes => sizes.Count())!.Key;
        var inSections = false;
        var appended = 0;
        var result = new List<LayoutPage>();
        // The last thing emitted, when it was a section heading that the next line may continue.
        (int Page, int Index, string Number, string Heading)? open = null;

        foreach (var page in pages)
        {
            var items = new List<LayoutItem>();
            foreach (var item in page.Items)
            {
                if (item is not LayoutLine { Bold: true } line || line.Text.Contains("....", StringComparison.Ordinal))
                {
                    // A line of dots is an entry in the table of contents.
                    items.Add(item);
                    open = item is LayoutLine ? null : open;
                    continue;
                }

                if (line.FontSize > body + 2 && Section().Match(line.Text) is { Success: true } section)
                {
                    inSections = true;
                    open = (page.Number, items.Count, $"§ {section.Groups["number"].Value}", Title(section.Groups["heading"].Value));
                    items.Add(line with { Text = $"{open.Value.Number}. {open.Value.Heading}" });
                }
                else if (line.FontSize > body + 2 && Capitals().IsMatch(line.Text) && inSections)
                {
                    if (open is { } heading && heading.Page == page.Number)
                    {
                        // A heading too long for one line carries on in the next.
                        open = heading with { Heading = Lines.Join(heading.Heading, line.Text.ToLowerInvariant()) };
                        items[heading.Index] = line with { Text = $"{heading.Number}. {open.Value.Heading}" };
                    }
                    else
                    {
                        open = (page.Number, items.Count, $"{Marker}{++appended}", Title(line.Text));
                        items.Add(line with { Text = $"{open.Value.Number}. {open.Value.Heading}" });
                        // What follows has no clause numbers of its own. It is one clause.
                        items.Add(line with { Text = "Stk. 1. ", Bold = false, Top = line.Top - 0.01 });
                    }
                }
                else if (appended == 0 && inSections && line.FontSize > body && Numbered().Match(line.Text) is { Success: true } clause)
                {
                    open = null;
                    // Set apart from the text under it, so the heading stands as a paragraph of its own.
                    items.Add(line with { Text = $"Stk. {clause.Groups["number"].Value}. {clause.Groups["heading"].Value}", Top = line.Top + 1000 });
                }
                else
                {
                    open = null;
                    items.Add(line);
                }
            }

            result.Add(page with { Items = items });
        }

        return result;
    }

    /// <summary>Begins the heading of an appended agreement, in the form the splitter reads as a section.</summary>
    public const string Marker = "Aftale ";

    private static string Title(string capitals) =>
        capitals.Length == 0 ? capitals : string.Concat(capitals[..1].ToUpperInvariant(), capitals[1..].ToLowerInvariant());

    [GeneratedRegex(@"^Stk\. 1\. ")]
    private static partial Regex StkClause();

    [GeneratedRegex(@"^§ ?(?<number>\d+) (?<heading>\P{Ll}+)$")]
    private static partial Regex Section();

    [GeneratedRegex(@"^(?<number>\d{1,2})\. (?<heading>\p{Lu}.*)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^\P{Ll}{4,}$")]
    private static partial Regex Capitals();
}

using System.Text.RegularExpressions;
using Overens.Documents;

namespace Overens.Ingestion.Reading;

/// <summary>The pages a clause stands on.</summary>
public readonly record struct PageSpan(int First, int Last);

/// <summary>An agreement as read from its pages: its sections and clauses, and where each clause stands.</summary>
public sealed record ReadAgreement(
    string Title,
    IReadOnlyList<Section> Sections,
    IReadOnlyDictionary<string, PageSpan> Pages);

/// <summary>
/// Turns page layout into numbered clauses. It drops what is not the agreement's text (the
/// front page, the table of contents, running titles and page numbers) and puts back
/// together what the pages split: a clause that runs over a page break, a table that
/// continues under a repeated header.
///
/// No model is involved. Every later step cites clauses by the ids made here.
/// </summary>
public static partial class ClauseSegmenter
{
    public static ReadAgreement Segment(IReadOnlyList<LayoutPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var furniture = Furniture(pages);
        var sections = new List<Section>();
        var spans = new Dictionary<string, PageSpan>();

        (string Number, string Heading)? section = null;
        var clauses = new List<Clause>();
        string? clauseId = null;
        var blocks = new List<Block>();
        string? paragraph = null;
        (int Page, double Top, double FontSize)? last = null;
        var tableEndedOn = 0;

        foreach (var page in pages)
        {
            foreach (var item in page.Items)
            {
                if (item is LayoutLine { } furnitureLine && furniture.Contains(Key(furnitureLine)))
                {
                    continue;
                }

                switch (item)
                {
                    case LayoutLine line when SectionHeading().Match(line.Text) is { Success: true } heading:
                        EndSection();
                        var number = heading.Groups["paragraph"].Success ? $"§ {heading.Groups["paragraph"].Value}" : heading.Groups["protocol"].Value;
                        section = (number, heading.Groups["heading"].Value);
                        break;

                    case LayoutLine line when section is not null && ClauseStart().Match(line.Text) is { Success: true } start:
                        EndClause();
                        clauseId = $"{section.Value.Number}, stk. {start.Groups["number"].Value}";
                        paragraph = start.Groups["text"].Value;
                        spans[clauseId] = new(page.Number, page.Number);
                        last = (page.Number, line.Top, line.FontSize);
                        break;

                    case LayoutLine line when clauseId is not null:
                        // More space than a line break leaves, on the same page, starts a new paragraph.
                        var gap = last is { } before && before.Page == page.Number && before.Top - line.Top > before.FontSize * 1.6;
                        if (paragraph is null || gap)
                        {
                            EndParagraph();
                            paragraph = line.Text;
                        }
                        else
                        {
                            paragraph = Lines.Join(paragraph, line.Text);
                        }

                        spans[clauseId] = spans[clauseId] with { Last = page.Number };
                        last = (page.Number, line.Top, line.FontSize);
                        break;

                    case LayoutTable table when clauseId is not null && table.Rows.Count > 0:
                        EndParagraph();
                        // A table that carries on over a page break repeats its header on the new page.
                        if (blocks is [.., TableBlock open] && tableEndedOn == page.Number - 1 && open.Header.SequenceEqual(table.Rows[0]))
                        {
                            blocks[^1] = open with { Rows = [.. open.Rows, .. table.Rows.Skip(1)] };
                        }
                        else
                        {
                            blocks.Add(new TableBlock(table.Rows[0], [.. table.Rows.Skip(1)]));
                        }

                        tableEndedOn = page.Number;
                        spans[clauseId] = spans[clauseId] with { Last = page.Number };
                        last = null;
                        break;

                    default:
                        // Text before the first clause of a section: the front page, a table of contents.
                        break;
                }
            }
        }

        EndSection();
        var title = pages.Count == 0
            ? ""
            : pages[0].Items.OfType<LayoutLine>().Where(line => line.FontSize >= 18).Select(line => line.Text).Aggregate("", Lines.Join);
        return new(title, sections, spans);

        void EndParagraph()
        {
            if (!string.IsNullOrWhiteSpace(paragraph))
            {
                blocks.Add(new Paragraph(paragraph));
            }

            paragraph = null;
        }

        void EndClause()
        {
            EndParagraph();
            if (clauseId is not null)
            {
                clauses.Add(new(clauseId, [.. blocks]));
            }

            (clauseId, last) = (null, null);
            blocks.Clear();
        }

        void EndSection()
        {
            EndClause();
            // A heading with no clauses under it was an entry in the table of contents.
            if (section is { } ended && clauses.Count > 0)
            {
                sections.Add(new(ended.Number, ended.Heading, [.. clauses]));
            }

            clauses.Clear();
            section = null;
        }
    }

    /// <summary>
    /// Lines that stand at the same height on at least half the pages, numbers aside: running
    /// titles and page numbers.
    /// </summary>
    private static HashSet<(double, string)> Furniture(IReadOnlyList<LayoutPage> pages)
    {
        if (pages.Count < 3)
        {
            return [];
        }

        return [.. pages
            .SelectMany(page => page.Items.OfType<LayoutLine>().Select(Key).Distinct())
            .GroupBy(key => key)
            .Where(group => group.Count() * 2 >= pages.Count)
            .Select(group => group.Key)];
    }

    private static (double, string) Key(LayoutLine line) => (Math.Round(line.Top), Digits().Replace(line.Text, "#"));

    [GeneratedRegex(@"^(?:§ ?(?<paragraph>\d+)|(?<protocol>Protokollat \d+))\. (?<heading>\S.*)$")]
    private static partial Regex SectionHeading();

    [GeneratedRegex(@"^Stk\. (?<number>\d+)\. (?<text>.*)$")]
    private static partial Regex ClauseStart();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}

using System.Text;

namespace Overens.Documents;

/// <summary>An agreement as plain text, the way it reads on the page.</summary>
public static class AgreementText
{
    public static string Render(AgreementDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = new StringBuilder();
        text.AppendLine(document.Title).AppendLine(document.Parties);
        foreach (var section in document.Sections.Concat(document.Protocols))
        {
            text.AppendLine().Append(section.Number).Append(". ").AppendLine(section.Heading);
            foreach (var (clause, index) in section.Clauses.Select((clause, index) => (clause, index)))
            {
                text.AppendLine(Render(clause, index + 1));
            }
        }

        return text.ToString();
    }

    /// <summary>One clause: its number, its text, and any table as one row per line.</summary>
    public static string Render(Clause clause, int number)
    {
        ArgumentNullException.ThrowIfNull(clause);
        var lines = new List<string>();
        foreach (var block in clause.Blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    lines.Add(lines.Count == 0 ? $"Stk. {number}. {paragraph.Text}" : paragraph.Text);
                    break;
                case TableBlock table:
                    lines.Add(string.Join(" | ", table.Header));
                    lines.AddRange(table.Rows.Select(row => string.Join(" | ", row)));
                    break;
                default:
                    throw new NotSupportedException($"Unknown block {block.GetType().Name}.");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}

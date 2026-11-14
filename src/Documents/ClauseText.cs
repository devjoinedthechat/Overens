namespace Overens.Documents;

/// <summary>A clause's words as one text, with each table row on its own line. Quotes are checked against this.</summary>
public static class ClauseText
{
    public static string Of(Clause clause)
    {
        ArgumentNullException.ThrowIfNull(clause);
        return string.Join('\n', clause.Blocks.Select(block => block switch
        {
            Paragraph paragraph => paragraph.Text,
            TableBlock table => string.Join('\n', table.Rows.Prepend(table.Header).Select(row => string.Join(" | ", row))),
            _ => "",
        }));
    }
}

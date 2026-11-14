namespace Overens.Ingestion.Reading;

/// <summary>
/// What is on a page, top to bottom, before anyone has decided what it means: lines of text
/// and tables. Every way of reading a PDF produces this, so the steps after it do not care
/// which one was used.
/// </summary>
public sealed record LayoutPage(int Number, IReadOnlyList<LayoutItem> Items);

/// <param name="Top">Distance from the bottom of the page, in points. Higher is further up.</param>
public abstract record LayoutItem(double Top);

public sealed record LayoutLine(string Text, double Top, double FontSize, bool Bold) : LayoutItem(Top);

/// <summary>A table as rows of cells. The first row is the header.</summary>
public sealed record LayoutTable(IReadOnlyList<IReadOnlyList<string>> Rows, double Top) : LayoutItem(Top);

/// <summary>Reads the layout of a PDF.</summary>
public interface ILayoutReader
{
    Task<IReadOnlyList<LayoutPage>> ReadAsync(ReadOnlyMemory<byte> pdf, CancellationToken cancellationToken = default);
}

/// <summary>Joins lines of running text the way the words stood before they were wrapped.</summary>
public static class Lines
{
    /// <summary>
    /// A line that ends in a hyphen was broken at it, so the next line follows without a
    /// space: "17.00-" and "22.00". A Danish suspended hyphen, as in "lager- og
    /// logistikarbejde", keeps its space. A word that was split to fit the line is put back
    /// together without the hyphen. Which of the last two a hyphen is cannot always be told
    /// from the page; this takes a lower-case letter on both sides to mean a split word.
    /// </summary>
    public static string Join(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Length == 0)
        {
            return after;
        }

        if (!before.EndsWith('-'))
        {
            return $"{before} {after}";
        }

        if (after.StartsWith("og ", StringComparison.Ordinal) || after.StartsWith("eller ", StringComparison.Ordinal))
        {
            return $"{before} {after}";
        }

        // A word broken in two for the line's sake ("virk-" / "somhed") loses its hyphen. A hyphen after a
        // digit or before a capital was there before the line was broken ("17.00-" / "22.00", "HK-" / "Handel").
        var broken = before.Length > 1 && char.IsLower(before[^2]) && after.Length > 0 && char.IsLower(after[0]);
        return broken ? before[..^1] + after : before + after;
    }
}

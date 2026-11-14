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
    /// space: "17.00-" and "22.00". The exception is a Danish suspended hyphen, as in
    /// "lager- og logistikarbejde", which keeps its space.
    /// </summary>
    public static string Join(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Length == 0)
        {
            return after;
        }

        var suspended = after.StartsWith("og ", StringComparison.Ordinal) || after.StartsWith("eller ", StringComparison.Ordinal);
        return before.EndsWith('-') && !suspended ? before + after : $"{before} {after}";
    }
}

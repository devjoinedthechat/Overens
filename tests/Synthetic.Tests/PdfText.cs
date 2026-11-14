using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Overens.Synthetic.Tests;

/// <summary>Reads a PDF back with a different library from the one that wrote it.</summary>
internal static partial class PdfText
{
    /// <summary>The text of each page, with every run of white space as one space.</summary>
    public static IReadOnlyList<string> Pages(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return [.. document.GetPages().Select(page => Flatten(ContentOrderTextExtractor.GetText(page)))];
    }

    public static string Flatten(string text) => WhiteSpace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}

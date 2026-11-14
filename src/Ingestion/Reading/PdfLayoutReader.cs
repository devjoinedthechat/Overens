using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace Overens.Ingestion.Reading;

/// <summary>
/// Reads a PDF's layout on this machine, with no model and no service. Text comes from the
/// letters and their positions. Tables come from their ruled borders: the lines drawn on the
/// page form a grid, and each letter falls in one cell of it.
///
/// It reads PDFs that carry their text. A scanned page has no letters to read.
/// </summary>
public sealed class PdfLayoutReader : ILayoutReader
{
    public Task<IReadOnlyList<LayoutPage>> ReadAsync(ReadOnlyMemory<byte> pdf, CancellationToken cancellationToken = default)
    {
        using var document = PdfDocument.Open(pdf.ToArray());
        var pages = new List<LayoutPage>();
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages.Add(Read(page));
        }

        return Task.FromResult<IReadOnlyList<LayoutPage>>(pages);
    }

    private static LayoutPage Read(Page page)
    {
        var grids = Grids(page);
        var items = new List<LayoutItem>();
        var loose = new List<Letter>();
        var inCells = grids.ToDictionary(grid => grid, _ => new List<Letter>());
        foreach (var letter in page.Letters)
        {
            var at = Centre(letter);
            var grid = grids.FirstOrDefault(grid => grid.Contains(at));
            (grid is null ? loose : inCells[grid]).Add(letter);
        }

        foreach (var (grid, letters) in inCells)
        {
            var rows = new List<IReadOnlyList<string>>();
            for (var row = 0; row < grid.Rows; row++)
            {
                var cells = new string[grid.Columns];
                for (var column = 0; column < grid.Columns; column++)
                {
                    var inside = letters.Where(letter => grid.CellOf(Centre(letter)) == (row, column)).ToList();
                    cells[column] = TextLines(inside).Select(line => line.Text).Aggregate("", Lines.Join);
                }

                rows.Add(cells);
            }

            items.Add(new LayoutTable(rows, grid.Top));
        }

        items.AddRange(TextLines(loose));
        return new(page.Number, [.. items.OrderByDescending(item => item.Top)]);
    }

    /// <summary>Letters as lines of text: those on one baseline, left to right, with spaces where there are gaps.</summary>
    private static List<LayoutLine> TextLines(List<Letter> letters)
    {
        var lines = new List<LayoutLine>();
        foreach (var onLine in letters
            .Where(letter => !string.IsNullOrWhiteSpace(letter.Value))
            .GroupBy(letter => Math.Round(letter.StartBaseLine.Y))
            .OrderByDescending(line => line.Key))
        {
            var text = new System.Text.StringBuilder();
            Letter? previous = null;
            foreach (var letter in onLine.OrderBy(letter => letter.StartBaseLine.X))
            {
                // A gap wider than a seventh of the type size is a space; kerning is far narrower.
                // Measured along the baseline, where slanted letters do not lean into each other.
                if (previous is not null && letter.StartBaseLine.X - previous.EndBaseLine.X > previous.PointSize * 0.14)
                {
                    text.Append(' ');
                }

                text.Append(letter.Value);
                previous = letter;
            }

            var first = onLine.MinBy(letter => letter.StartBaseLine.X)!;
            // The size most of the line is set in, so that a few slanted letters do not decide it.
            var size = onLine.GroupBy(letter => Math.Round(letter.PointSize, 1)).MaxBy(sizes => sizes.Count())!.Key;
            lines.Add(new(text.ToString(), onLine.Key, size, first.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return lines;
    }

    /// <summary>The ruled tables on a page, each as the grid its border lines form.</summary>
    private static List<Grid> Grids(Page page)
    {
        var strokes = page.Paths
            .Where(path => path.IsStroked)
            .Select(path => path.GetBoundingRectangle())
            .OfType<PdfRectangle>()
            .Where(box => (box.Width < 1.5) != (box.Height < 1.5))
            .ToList();

        // Border lines that touch belong to the same table.
        var groups = new List<List<PdfRectangle>>();
        foreach (var stroke in strokes)
        {
            var touching = groups.Where(group => group.Any(other => Touch(stroke, other))).ToList();
            var merged = touching.SelectMany(group => group).Append(stroke).ToList();
            groups.RemoveAll(touching.Contains);
            groups.Add(merged);
        }

        var grids = new List<Grid>();
        foreach (var group in groups)
        {
            var across = Distinct(group.Where(box => box.Height < 1.5).Select(box => box.Bottom));
            var down = Distinct(group.Where(box => box.Width < 1.5).Select(box => box.Left));
            if (across.Count >= 2 && down.Count >= 2)
            {
                grids.Add(new(down, [.. across.OrderByDescending(y => y)]));
            }
        }

        return grids;

        static bool Touch(PdfRectangle first, PdfRectangle second) =>
            first.Left - 2 <= second.Right && second.Left - 2 <= first.Right
            && first.Bottom - 2 <= second.Top && second.Bottom - 2 <= first.Top;

        // Positions within a point and a half of each other are the same line.
        static List<double> Distinct(IEnumerable<double> positions)
        {
            var kept = new List<double>();
            foreach (var position in positions.Order())
            {
                if (kept.Count == 0 || position - kept[^1] > 1.5)
                {
                    kept.Add(position);
                }
            }

            return kept;
        }
    }

    private static PdfPoint Centre(Letter letter) =>
        new((letter.BoundingBox.Left + letter.BoundingBox.Right) / 2, (letter.BoundingBox.Bottom + letter.BoundingBox.Top) / 2);

    /// <summary>Column edges, left to right, and row edges, top to bottom.</summary>
    private sealed record Grid(IReadOnlyList<double> Xs, IReadOnlyList<double> Ys)
    {
        public int Rows => Ys.Count - 1;

        public int Columns => Xs.Count - 1;

        public double Top => Ys[0];

        public bool Contains(PdfPoint point) => CellOf(point) is not null;

        public (int Row, int Column)? CellOf(PdfPoint point)
        {
            for (var row = 0; row < Rows; row++)
            {
                for (var column = 0; column < Columns; column++)
                {
                    if (point.X > Xs[column] && point.X < Xs[column + 1] && point.Y < Ys[row] && point.Y > Ys[row + 1])
                    {
                        return (row, column);
                    }
                }
            }

            return null;
        }
    }
}

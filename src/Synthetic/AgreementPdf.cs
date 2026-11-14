using System.Reflection;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using Overens.Documents;
using PdfSharp.Fonts;

namespace Overens.Synthetic;

/// <summary>
/// An agreement as a PDF laid out like a printed one: a front page, a table of contents, a
/// running header and page numbers, and ruled tables. Everything a reader of the PDF has to
/// see past to find the clauses.
/// </summary>
public static class AgreementPdf
{
    private const string Family = "Liberation Serif";

    /// <summary>Printed on the front page, so the document cannot be mistaken for a real agreement.</summary>
    public const string Notice = "Syntetisk testdokument. Organisationerne og overenskomsten findes ikke.";
    private static readonly Lock FontLock = new();

    public static byte[] Render(AgreementDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (FontLock)
        {
            GlobalFontSettings.FontResolver ??= new EmbeddedFonts();
        }

        var pdf = new Document();
        pdf.Info.Title = document.Title;
        var normal = pdf.Styles[StyleNames.Normal]!;
        normal.Font.Name = Family;
        normal.Font.Size = 11;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
        normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing = 1.15;

        var page = pdf.AddSection();
        page.PageSetup = pdf.DefaultPageSetup.Clone();
        page.PageSetup.PageFormat = PageFormat.A4;
        page.PageSetup.LeftMargin = page.PageSetup.RightMargin = Unit.FromCentimeter(2.5);
        page.PageSetup.TopMargin = Unit.FromCentimeter(3);
        page.PageSetup.BottomMargin = Unit.FromCentimeter(2.5);
        page.PageSetup.DifferentFirstPageHeaderFooter = true;

        var header = page.Headers.Primary.AddParagraph(document.Title);
        header.Format.Font.Size = 9;
        header.Format.Alignment = ParagraphAlignment.Right;
        var footer = page.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 9;
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.AddText("Side ");
        footer.AddPageField();
        footer.AddText(" af ");
        footer.AddNumPagesField();

        // Front page.
        var title = page.AddParagraph(document.Title);
        title.Format.Font.Size = 24;
        title.Format.Font.Bold = true;
        title.Format.SpaceBefore = Unit.FromCentimeter(7);
        title.Format.Alignment = ParagraphAlignment.Center;
        var parties = page.AddParagraph(document.Parties);
        parties.Format.Font.Size = 13;
        parties.Format.Alignment = ParagraphAlignment.Center;
        var notice = page.AddParagraph(Notice);
        notice.Format.Font.Size = 9;
        notice.Format.SpaceBefore = Unit.FromCentimeter(12);
        notice.Format.Alignment = ParagraphAlignment.Center;

        // Table of contents, which repeats every heading before the text itself.
        var contents = page.AddParagraph("Indholdsfortegnelse");
        contents.Format.PageBreakBefore = true;
        contents.Format.Font.Size = 15;
        contents.Format.Font.Bold = true;
        foreach (var section in document.Sections.Concat(document.Protocols))
        {
            var entry = page.AddParagraph($"{section.Number}. {section.Heading}");
            entry.Format.SpaceAfter = Unit.FromPoint(2);
        }

        var first = true;
        foreach (var section in document.Sections.Concat(document.Protocols))
        {
            var heading = page.AddParagraph($"{section.Number}. {section.Heading}");
            heading.Format.Font.Size = 13;
            heading.Format.Font.Bold = true;
            heading.Format.SpaceBefore = Unit.FromPoint(14);
            heading.Format.KeepWithNext = true;
            heading.Format.PageBreakBefore = first;
            first = false;

            foreach (var (clause, index) in section.Clauses.Select((clause, index) => (clause, index)))
            {
                var opening = true;
                foreach (var block in clause.Blocks)
                {
                    switch (block)
                    {
                        case Documents.Paragraph text:
                            var paragraph = page.AddParagraph();
                            if (opening)
                            {
                                paragraph.AddFormattedText($"Stk. {index + 1}. ", TextFormat.Italic);
                            }

                            paragraph.AddText(text.Text);
                            break;
                        case TableBlock table:
                            Add(page, table);
                            break;
                        default:
                            throw new NotSupportedException($"Unknown block {block.GetType().Name}.");
                    }

                    opening = false;
                }
            }
        }

        var renderer = new PdfDocumentRenderer { Document = pdf };
        renderer.RenderDocument();
        // A fixed date, so the file says nothing about when or where it was made.
        renderer.PdfDocument.Info.CreationDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        renderer.PdfDocument.Info.ModificationDate = renderer.PdfDocument.Info.CreationDate;
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream);
        return stream.ToArray();
    }

    private static void Add(MigraDoc.DocumentObjectModel.Section page, TableBlock block)
    {
        var table = page.AddTable();
        table.Borders.Width = 0.5;
        table.Format.Font.Size = 10;
        table.Format.SpaceAfter = Unit.FromPoint(2);
        table.TopPadding = table.BottomPadding = Unit.FromPoint(3);

        // The first column holds the labels and gets the room the others do not need.
        var width = Unit.FromCentimeter(16);
        var others = block.Header.Count - 1;
        var label = others == 0 ? width : Unit.FromCentimeter(Math.Max(4.5, 16 - (others * 3.2)));
        table.AddColumn(label);
        for (var column = 0; column < others; column++)
        {
            table.AddColumn(Unit.FromCentimeter((16 - label.Centimeter) / others));
        }

        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Format.Font.Bold = true;
        head.Shading.Color = Colors.LightGray;
        Fill(head, block.Header);
        foreach (var cells in block.Rows)
        {
            Fill(table.AddRow(), cells);
        }

        page.AddParagraph().Format.SpaceAfter = Unit.FromPoint(4);

        static void Fill(Row row, IReadOnlyList<string> cells)
        {
            for (var column = 0; column < cells.Count; column++)
            {
                row.Cells[column].AddParagraph(cells[column]);
            }
        }
    }

    /// <summary>Serves the fonts built into this assembly, so the PDF looks the same on every machine.</summary>
    private sealed class EmbeddedFonts : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) =>
            // There is no italic face here; the bold or regular face is slanted instead.
            new(bold ? "LiberationSerif-Bold" : "LiberationSerif-Regular", false, italic);

        public byte[] GetFont(string faceName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var font = assembly.GetManifestResourceStream($"Overens.Synthetic.Fonts.{faceName}.ttf")
                ?? throw new InvalidOperationException($"The font {faceName} is not built in.");
            using var bytes = new MemoryStream();
            font.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}

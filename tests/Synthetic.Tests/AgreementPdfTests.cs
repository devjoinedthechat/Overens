using Overens.Documents;

namespace Overens.Synthetic.Tests;

public sealed class AgreementPdfTests
{
    public static TheoryData<int> Difficulties => [1, 2, 3];

    [Theory]
    [MemberData(nameof(Difficulties))]
    public void The_pdf_holds_every_clause_and_every_table_cell(int difficulty)
    {
        var agreement = AgreementGenerator.Generate(new("pdf", 31 + difficulty, difficulty, 2026));

        var text = string.Join(" ", PdfText.Pages(AgreementPdf.Render(agreement.Document)));

        var blocks = agreement.Document.Sections.Concat(agreement.Document.Protocols)
            .SelectMany(section => section.Clauses)
            .SelectMany(clause => clause.Blocks);
        Assert.All(blocks, block =>
        {
            var parts = block switch
            {
                Paragraph paragraph => [paragraph.Text],
                TableBlock table => table.Header.Concat(table.Rows.SelectMany(row => row)),
                _ => [],
            };
            Assert.All(parts, part => Assert.Contains(PdfText.Flatten(part), text, StringComparison.Ordinal));
        });
        Assert.All(agreement.Truth.Rules, rule => Assert.Contains(PdfText.Flatten(rule.Source.Quote), text, StringComparison.Ordinal));
    }

    [Fact]
    public void The_pdf_is_laid_out_like_a_printed_agreement()
    {
        var agreement = AgreementGenerator.Generate(new("pdf", 34, 3, 2026));

        var pages = PdfText.Pages(AgreementPdf.Render(agreement.Document));

        Assert.InRange(pages.Count, 4, 12);
        // A front page with no header, then a table of contents that names every section.
        Assert.DoesNotContain("Side 1", pages[0], StringComparison.Ordinal);
        Assert.Contains(AgreementPdf.Notice, pages[0], StringComparison.Ordinal);
        Assert.Contains("Indholdsfortegnelse", pages[1], StringComparison.Ordinal);
        Assert.All(agreement.Document.Sections, section =>
            Assert.Contains($"{section.Number}. {section.Heading}", pages[1], StringComparison.Ordinal));
        // Every later page repeats the title and carries a page number.
        Assert.All(pages.Skip(1).Select((page, index) => (page, number: index + 2)), page =>
        {
            Assert.Contains(agreement.Document.Title, page.page, StringComparison.Ordinal);
            Assert.Contains($"Side {page.number} af {pages.Count}", page.page, StringComparison.Ordinal);
        });
    }
}

using Overens.Documents;
using Overens.Ingestion.Reading;
using Overens.Rules;
using Overens.Synthetic;

namespace Overens.Ingestion.Tests;

public sealed class ReadingTests
{
    public static TheoryData<int, int> SeedsAndDifficulties
    {
        get
        {
            var data = new TheoryData<int, int>();
            for (var seed = 1; seed <= 30; seed++)
            {
                data.Add(seed, (seed % 3) + 1);
            }

            return data;
        }
    }

    private static async Task<ReadAgreement> Read(AgreementDocument document) =>
        ClauseSegmenter.Segment(await new PdfLayoutReader().ReadAsync(AgreementPdf.Render(document)));

    /// <summary>
    /// The generator knows the structure it laid out. Reading the PDF must give that structure
    /// back exactly: every clause, every paragraph word for word, every table cell.
    /// </summary>
    [Theory]
    [MemberData(nameof(SeedsAndDifficulties))]
    public async Task Reading_the_pdf_gives_back_the_agreement_that_was_printed(int seed, int difficulty)
    {
        var document = AgreementGenerator.Generate(new($"read-{seed}", seed, difficulty, 2026)).Document;

        var read = await Read(document);

        Assert.Equal(document.Title, read.Title);
        Assert.Equal(RuleJson.Write(document.Sections.Concat(document.Protocols)), RuleJson.Write(read.Sections));
    }

    [Fact]
    public async Task Every_clause_knows_its_pages_and_none_is_on_the_front_page_or_in_the_contents()
    {
        var document = Corpus.Generate(Corpus.Agreements[2]).Document;

        var read = await Read(document);

        var ids = document.Sections.Concat(document.Protocols).SelectMany(section => section.Clauses).Select(clause => clause.Id);
        Assert.Equal(ids.Order(), read.Pages.Keys.Order());
        Assert.All(read.Pages.Values, span => Assert.InRange(span.First, 3, span.Last));
    }

    /// <summary>The round trips above are only worth something if some clauses really are split by a page break.</summary>
    [Fact]
    public async Task Some_of_the_generated_agreements_have_clauses_that_run_over_a_page_break()
    {
        var split = 0;
        for (var seed = 1; seed <= 30; seed++)
        {
            var read = await Read(AgreementGenerator.Generate(new($"read-{seed}", seed, (seed % 3) + 1, 2026)).Document);
            split += read.Pages.Values.Count(span => span.Last > span.First);
        }

        Assert.InRange(split, 5, 200);
    }
}

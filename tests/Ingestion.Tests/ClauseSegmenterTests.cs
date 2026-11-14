using Overens.Documents;
using Overens.Ingestion.Reading;

namespace Overens.Ingestion.Tests;

/// <summary>Layouts written by hand, for the cases the generated agreements do not happen to produce.</summary>
public sealed class ClauseSegmenterTests
{
    private static LayoutLine Line(string text, double top) => new(text, top, 11, false);

    [Fact]
    public void A_table_that_runs_over_a_page_break_is_one_table()
    {
        var read = ClauseSegmenter.Segment(
        [
            new(1,
            [
                Line("§ 4. Løn", 700),
                Line("Stk. 1. Mindstebetalingen udgør:", 680),
                new LayoutTable([["Anciennitet", "Sats"], ["Under 1 år", "140,00 kr."]], 660),
            ]),
            new(2,
            [
                new LayoutTable([["Anciennitet", "Sats"], ["Efter 1 år", "148,50 kr."]], 780),
                Line("Stk. 2. Anciennitet regnes i hele måneder.", 700),
            ]),
        ]);

        var clause = read.Sections[0].Clauses[0];
        var table = Assert.IsType<TableBlock>(clause.Blocks[1]);
        Assert.Equal([["Under 1 år", "140,00 kr."], ["Efter 1 år", "148,50 kr."]], table.Rows);
        Assert.Equal(new PageSpan(1, 2), read.Pages["§ 4, stk. 1"]);
        Assert.Equal(2, read.Sections[0].Clauses.Count);
    }

    [Fact]
    public void A_clause_can_hold_several_paragraphs_and_a_paragraph_can_cross_a_page()
    {
        var read = ClauseSegmenter.Segment(
        [
            new(1,
            [
                Line("§ 7. Pension", 200),
                Line("Stk. 1. Arbejdsgiveren indbetaler 8,0 pct. af", 180),
                Line("lønnen til pensionsordningen.", 165.4),
                Line("Bidraget indbetales månedligt, og det gælder for vagter kl. 17.00-", 145),
            ]),
            new(2, [Line("22.00 og for det selskab, som parterne har aftalt for lager-", 780), Line("og logistikområdet.", 765.4)]),
        ]);

        Assert.Equal(
            [
                new Paragraph("Arbejdsgiveren indbetaler 8,0 pct. af lønnen til pensionsordningen."),
                new Paragraph("Bidraget indbetales månedligt, og det gælder for vagter kl. 17.00-22.00 og for det selskab, som parterne har aftalt for lager- og logistikområdet."),
            ],
            read.Sections[0].Clauses[0].Blocks);
    }

    [Fact]
    public void Running_titles_page_numbers_and_the_table_of_contents_are_left_out()
    {
        LayoutPage Page(int number, params LayoutItem[] items) =>
            new(number, [Line("Overenskomst 2026-2029", 800), .. items, Line($"Side {number} af 3", 40)]);

        var read = ClauseSegmenter.Segment(
        [
            Page(1, Line("Indholdsfortegnelse", 700), Line("§ 1. Område", 680), Line("§ 2. Løn", 665)),
            Page(2, Line("§ 1. Område", 700), Line("Stk. 1. Overenskomsten omfatter lagerarbejde", 680)),
            Page(3, Line("og terminalarbejde.", 780), Line("§ 2. Løn", 700), Line("Stk. 1. Lønnen udbetales månedligt.", 680)),
        ]);

        Assert.Equal(["§ 1", "§ 2"], read.Sections.Select(section => section.Number));
        Assert.Equal(
            new Paragraph("Overenskomsten omfatter lagerarbejde og terminalarbejde."),
            Assert.Single(read.Sections[0].Clauses[0].Blocks));
    }
}

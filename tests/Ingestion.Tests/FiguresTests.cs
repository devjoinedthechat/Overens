using Overens.Documents;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Reading;
using Overens.Rules;

namespace Overens.Ingestion.Tests;

public sealed class FiguresTests
{
    private static IEnumerable<(FigureKind Kind, decimal Value)> Read(string text) =>
        Figures.In(text).Select(figure => (figure.Kind, figure.Value));

    [Fact]
    public void Numbers_are_read_the_danish_way()
    {
        Assert.Equal([(FigureKind.Number, 142.50m), (FigureKind.Number, 12.5m), (FigureKind.Number, 1234.50m), (FigureKind.Number, 7m)],
            Read("142,50 kr., 12,5 pct., 1.234,50 kr. og 7 timer"));
        Assert.True(Figures.In("10,0 pct.")[0].Decimals);
        Assert.False(Figures.In("10 måneder")[0].Decimals);
    }

    [Fact]
    public void A_reference_a_date_and_an_ordinal_are_not_numbers()
    {
        var figures = Figures.In("jf. § 3, stk. 2, og Løntrin 4 fra 1. marts 2027 og den første januar 2026; Lærlinge, 2. år");

        Assert.Equal(
            [FigureKind.Reference, FigureKind.Reference, FigureKind.Reference, FigureKind.Date, FigureKind.Date, FigureKind.Ordinal],
            figures.Select(figure => figure.Kind));
        Assert.Equal([new DateOnly(2027, 3, 1), new DateOnly(2026, 1, 1)], figures.Where(figure => figure.Kind == FigureKind.Date).Select(figure => figure.Day!.Value));
        Assert.Equal(2m, figures[^1].Value);
    }

    [Fact]
    public void Dates_in_digits_and_without_a_year_are_dates()
    {
        var figures = Figures.In("Pr. 01.03.2027, fra 1.3.2028 og til 1. marts");

        Assert.All(figures, figure => Assert.Equal(FigureKind.Date, figure.Kind));
        Assert.Equal([new DateOnly(2027, 3, 1), new DateOnly(2028, 3, 1), null], figures.Select(figure => figure.Day));
    }

    [Fact]
    public void Times_of_day_are_clock_times_and_midnight_is_one_time()
    {
        var times = Figures.In("kl. 18.00-24.00 og 00:00-06.30").Select(figure => (figure.Kind, figure.Time));

        Assert.Equal([(FigureKind.Clock, (TimeOnly?)new TimeOnly(18, 0)), (FigureKind.Clock, TimeOnly.MinValue), (FigureKind.Clock, TimeOnly.MinValue), (FigureKind.Clock, new TimeOnly(6, 30))], times);
    }

    [Fact]
    public void Numbers_in_words_and_ordinals_in_words_are_read()
    {
        Assert.Equal([(FigureKind.Word, 8m), (FigureKind.Word, 18m), (FigureKind.Ordinal, 1m), (FigureKind.Ordinal, 2m)],
            Read("otte timer, atten måneder, første og anden overarbejdstime"));
    }

    [Fact]
    public void A_figure_at_the_end_of_a_sentence_is_a_number_and_not_an_ordinal()
    {
        Assert.Equal([(FigureKind.Number, 2m)], Read("Tillægget er 2. Herefter gælder stk. 3.").Where(figure => figure.Kind != FigureKind.Reference));
    }

    // Three things the earlier checks, which matched strings, got wrong. None of the recorded answers met them.
    private static IReadOnlyList<string> Problems(string text, Rule rule) =>
        [.. Grounding.Check(
            new ReadAgreement("Test", [new Section("§ 1", "", [new Clause("§ 1, stk. 1", [new Paragraph(text)])])], new Dictionary<string, PageSpan>()),
            [(rule, [], Reported.None)])[0].Problems.Select(problem => problem.Code)];

    private static readonly DateOnly First = new(2026, 3, 1);

    [Fact]
    public void A_percentage_with_two_decimals_is_not_met_by_one_rounded_to_one()
    {
        const string Quote = "Satsen gælder fra 1. marts 2026, og arbejdsgiveren indbetaler 8,8 pct.";

        // 8,75 was looked for as "8,8" too, a form of one decimal that rounds.
        Assert.Contains("value_not_in_cited_clauses", Problems(Quote, new SpecialSavingsRule("s", new(First), new("§ 1, stk. 1", Quote), 8.75m)));
        Assert.Empty(Problems(Quote, new SpecialSavingsRule("s", new(First), new("§ 1, stk. 1", Quote), 8.8m)));
    }

    [Fact]
    public void An_amount_with_a_thousands_mark_is_printed()
    {
        const string Quote = "Fra 1. marts 2026 udgør mindstelønnen 1.234,50 kr. pr. uge.";

        Assert.Empty(Problems(Quote, new MinimumWageRule("w", new(First), new("§ 1, stk. 1", Quote), [new(0, 1234.50m)])));
    }

    [Fact]
    public void The_second_year_is_not_a_count_of_two()
    {
        const string Quote = "Fra 1. marts 2026 gælder satsen for lærlinge, 2. år, og efter 1 års anciennitet 150,00 kr.";
        MinimumWageRule Wage(int months) => new("w", new(First), new("§ 1, stk. 1", Quote), [new(0, 140.00m), new(months, 150.00m)]);

        Assert.Contains("value_not_in_cited_clauses", Problems(Quote + " Mindstelønnen er 140,00 kr.", Wage(24)));
        Assert.Empty(Problems(Quote + " Mindstelønnen er 140,00 kr.", Wage(12)));
    }
}

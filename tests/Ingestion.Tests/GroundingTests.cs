using Overens.Documents;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Reading;
using Overens.Rules;

namespace Overens.Ingestion.Tests;

/// <summary>
/// The checks on a few clauses written out here, where each sentence is in view. The tests
/// in <see cref="ExtractionTests"/> run the same checks on whole generated agreements.
/// </summary>
public sealed class GroundingTests
{
    private static readonly DateOnly First = new(2026, 3, 1);
    private static readonly DateOnly Second = new(2027, 3, 1);

    private static ReadAgreement Agreement(params (string Id, string Text)[] clauses) => new(
        "Test",
        [.. clauses.GroupBy(clause => clause.Id.Split(',')[0]).Select(section =>
            new Section(section.Key, "", [.. section.Select(clause => new Clause(clause.Id, [new Paragraph(clause.Text)]))]))],
        new Dictionary<string, PageSpan>());

    private static IReadOnlyList<string> Problems(ReadAgreement agreement, Rule rule, decimal? increase = null, params Rule[] earlier) =>
        [.. Grounding.Check(agreement, [.. earlier.Select(before => (before, (IReadOnlyList<string>)[], Reported.None)), (rule, [], increase is { } raise ? new Reported(Increase: raise) : Reported.None)])[^1].Problems.Select(problem => problem.Code)];

    [Fact]
    public void A_number_that_names_a_clause_or_a_day_does_not_print_a_figure()
    {
        var agreement = Agreement(
            ("§ 5, stk. 1", "Overarbejde er arbejde ud over 7 timer og 24 minutter pr. dag, jf. § 3, stk. 2. Bestemmelsen gælder fra 1. marts 2026."),
            ("§ 5, stk. 2", "For de første to timers overarbejde pr. dag betales et tillæg på 50,0 pct. af timelønnen, herefter 100,0 pct. For arbejde på søndage betales 100,0 pct."));
        OvertimeRule Overtime(decimal firstHours) =>
            new("overtime", new(First), new("§ 5, stk. 2", "For de første to timers overarbejde pr. dag betales et tillæg på 50,0 pct. af timelønnen"), 7.4m, [new(firstHours, 50), new(null, 100)], 100);

        Assert.Empty(Problems(agreement, Overtime(2)));
        // A "3" is printed, in "§ 3". That is not three hours of overtime.
        Assert.Equal(["value_not_in_cited_clauses"], Problems(agreement, Overtime(3)));
    }

    [Fact]
    public void A_rate_added_to_a_flagged_rate_is_flagged_too()
    {
        var agreement = Agreement(
            ("§ 4, stk. 1", "Mindstelønnen udgør 140,00 kr. pr. time. Satsen gælder fra 1. marts 2026."),
            ("§ 4, stk. 2", "Satsen forhøjes pr. 1. marts 2027 med 4,00 kr. pr. time."));
        MinimumWageRule Wage(string id, DateOnly from, DateOnly? until, decimal rate, string clause, string quote) =>
            new(id, new(from, until), new(clause, quote), [new(0, rate)]);
        MinimumWageRule Later(decimal rate) => Wage("wage-2027", Second, null, rate, "§ 4, stk. 2", "Satsen forhøjes pr. 1. marts 2027 med 4,00 kr. pr. time.");

        var right = Wage("wage-2026", First, Second, 140.00m, "§ 4, stk. 1", "Mindstelønnen udgør 140,00 kr. pr. time.");
        Assert.Empty(Problems(agreement, Later(144.00m), increase: 4.00m, right));

        // The first year's rate is not the agreement's. The increase on it is, and the sum is still not to be trusted.
        var invented = right with { Bands = [new(0, 141.00m)] };
        Assert.Equal(["built_on_flagged_rule"], Problems(agreement, Later(145.00m), increase: 4.00m, invented));
    }

    [Fact]
    public void A_figure_given_from_another_day_in_running_text_keeps_the_rule_out()
    {
        var agreement = Agreement(
            ("§ 9, stk. 1", "Til medarbejderens fritvalgskonto indbetaler arbejdsgiveren 5,0 pct. fra 1. marts 2026 og 5,5 pct. fra 1. marts 2027."));
        SpecialSavingsRule Savings(DateOnly from, decimal percent) =>
            new("savings", new(from), new("§ 9, stk. 1", "Til medarbejderens fritvalgskonto indbetaler arbejdsgiveren 5,0 pct. fra 1. marts 2026"), percent);

        Assert.Empty(Problems(agreement, Savings(First, 5.0m)));
        Assert.Empty(Problems(agreement, Savings(Second, 5.5m)));
        // Both figures are printed in the clause. Each is given its own day.
        Assert.Equal(["value_under_another_date"], Problems(agreement, Savings(First, 5.5m)));
        Assert.Equal(["value_under_another_date"], Problems(agreement, Savings(Second, 5.0m)));
    }

    [Fact]
    public void A_table_row_that_holds_two_windows_is_read_for_each_of_them()
    {
        var table = new TableBlock(["Tidsrum", "Fra 1. marts 2026"], [["Lørdag kl. 14.00-24.00 og søndag kl. 05.00-24.00", "31,10 kr."]]);
        var agreement = new ReadAgreement(
            "Test",
            [new Section("§ 6", "", [new Clause("§ 6, stk. 1", [new Paragraph("For arbejde i de nævnte tidsrum betales nedenstående tillæg pr. time."), table])])],
            new Dictionary<string, PageSpan>());
        InconvenientHoursRule Rule(TimeOnly from, TimeOnly to) => new(
            "hours", new(First), new("§ 6, stk. 1", "For arbejde i de nævnte tidsrum betales nedenstående tillæg pr. time."),
            [new([DayOfWeek.Saturday], new(14, 0), new(0, 0), 31.10m), new([DayOfWeek.Sunday], from, to, 31.10m)]);

        // Sunday 05.00 to 24.00: the row prints a "24.00" before the "05.00", which belongs to Saturday.
        Assert.Empty(Problems(agreement, Rule(new(5, 0), new(0, 0))));
    }

    [Fact]
    public void A_date_in_words_or_written_short_is_a_printed_date()
    {
        HolidayAllowanceRule Holiday(DateOnly from) => new("holiday", new(from), new("§ 8, stk. 1", "Feriegodtgørelsen udgør 12,5 pct. af lønnen."), 12.5m);
        ReadAgreement Saying(string when) => Agreement(("§ 8, stk. 1", $"Feriegodtgørelsen udgør 12,5 pct. af lønnen. Satsen gælder fra {when}."));

        Assert.Empty(Problems(Saying("den første januar 2026"), Holiday(new(2026, 1, 1))));
        Assert.Empty(Problems(Saying("1.3.2026"), Holiday(new(2026, 3, 1))));
        Assert.Empty(Problems(Saying("01.03.2026"), Holiday(new(2026, 3, 1))));
        // "Den første" is the first of a month, and no other day.
        Assert.Equal(["date_not_in_agreement"], Problems(Saying("den første januar 2026"), Holiday(new(2026, 1, 2))));
    }

    [Fact]
    public void A_daily_norm_of_whole_hours_and_tiers_told_by_their_first_and_last_hour_are_printed()
    {
        var agreement = Agreement(
            ("§ 1, stk. 1", "Overenskomsten gælder fra 1. marts 2026."),
            ("§ 8, stk. 1", "Ved opgørelsen af overarbejde regnes 8 timer som den daglige arbejdstid."),
            ("§ 8, stk. 2", "Overarbejde betales med et tillæg på 50 pct. for første og anden overarbejdstime, 75 pct. for tredje og fjerde overarbejdstime og 100 pct. derudover. For arbejde på søndage betales 100 pct."));
        OvertimeRule Overtime(decimal norm, decimal second) =>
            new("overtime", new(First), new("§ 8, stk. 2", "For arbejde på søndage betales 100 pct."), norm, [new(2, 50), new(second, 75), new(null, 100)], 100);

        Assert.Empty(Problems(agreement, Overtime(8, 2)));
        // A second tier of three hours would run to the fifth hour, which is printed nowhere.
        Assert.Equal(["value_not_in_cited_clauses"], Problems(agreement, Overtime(8, 3)));
        Assert.Equal(["value_not_in_cited_clauses"], Problems(agreement, Overtime(7.5m, 2)));
    }

    [Fact]
    public void A_rate_in_a_table_of_grades_is_not_held_to_a_row_and_one_in_a_table_of_seniority_is()
    {
        ReadAgreement With(params string[] labels) => new(
            "Test",
            [
                new Section("§ 1", "", [new Clause("§ 1, stk. 1", [new Paragraph("Overenskomsten gælder fra 1. marts 2026.")])]),
                new Section("§ 7", "",
                [
                    new Clause("§ 7, stk. 1", [new Paragraph("Løntrin 2 nås efter et års ansættelse.")]),
                    new Clause("§ 7, stk. 2", [new Paragraph("Mindstelønnen fremgår af tabellen."), new TableBlock(["Trin", "Fra 1. marts 2026"], [[labels[0], "160,00 kr."], [labels[1], "170,00 kr."]])]),
                ]),
            ],
            new Dictionary<string, PageSpan>());
        MinimumWageRule Wage(decimal starting, decimal after) =>
            new("wage", new(First), new("§ 7, stk. 2", "Mindstelønnen fremgår af tabellen."), [new(0, starting), new(12, after)]);

        // Grades: where a rate stands says nothing about seniority, which § 7, stk. 1 states.
        Assert.Empty(Problems(With("Løntrin 1", "Løntrin 2"), Wage(160.00m, 170.00m)));
        // Seniority in the rows: the rate after a year must stand in the row for a year.
        Assert.Empty(Problems(With("0-11 måneders anciennitet", "Fra 12 måneders anciennitet"), Wage(160.00m, 170.00m)));
        Assert.Contains("value_in_another_row", Problems(With("0-11 måneders anciennitet", "Fra 12 måneders anciennitet"), Wage(170.00m, 160.00m)));
        // A known limit: "Under 1 år" prints the 1 of one year, so a rate swapped into that row is not told apart.
        Assert.DoesNotContain("value_in_another_row", Problems(With("Under 1 år", "Efter 1 år"), Wage(170.00m, 160.00m)));
    }

    [Fact]
    public void A_rows_label_is_every_cell_before_its_figure()
    {
        var table = new TableBlock(["Dage", "Tidsrum", "Fra 1. marts 2026"], [["Mandag-fredag", "kl. 15.00-23.00", "19,60 kr."], ["Lørdag", "kl. 06.00-24.00", "24,30 kr."]]);
        var agreement = new ReadAgreement(
            "Test",
            [new Section("§ 10", "", [new Clause("§ 10, stk. 1", [new Paragraph("For arbejde uden for dagtid betales et tillæg pr. time efter tabellen."), table])])],
            new Dictionary<string, PageSpan>());
        DayOfWeek[] weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
        InconvenientHoursRule Rule(decimal evening, decimal saturday) => new(
            "hours", new(First), new("§ 10, stk. 1", "For arbejde uden for dagtid betales et tillæg pr. time efter tabellen."),
            [new(weekdays, new(15, 0), new(23, 0), evening), new([DayOfWeek.Saturday], new(6, 0), new(0, 0), saturday)]);

        Assert.DoesNotContain("value_in_another_row", Problems(agreement, Rule(19.60m, 24.30m)));
        Assert.Contains("value_in_another_row", Problems(agreement, Rule(24.30m, 19.60m)));
    }
}

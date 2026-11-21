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
        [.. Grounding.Check(agreement, [.. earlier.Select(before => (before, (IReadOnlyList<string>)[], (decimal?)null, (string?)null)), (rule, [], increase, null)])[^1].Problems.Select(problem => problem.Code)];

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
}

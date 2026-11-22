using Overens.Ingestion.Extraction;
using Overens.Rules;

namespace Overens.Ingestion.Tests;

public sealed class SecondReadingTests
{
    private static readonly Source Clause = new("§ 1, stk. 1", "tekst");
    private static readonly DateOnly First = new(2026, 3, 1);
    private static readonly DateOnly Second = new(2027, 3, 1);

    private static HolidayAllowanceRule Holiday(string id, DateOnly from, DateOnly? until, decimal percent) => new(id, new(from, until), Clause, percent);

    private static IEnumerable<string> Differs(IReadOnlyList<Rule> first, IReadOnlyList<Rule> second) =>
        SecondReading.Compare(first, second).Select(problem => problem.RuleId);

    [Fact]
    public void Two_readings_that_say_the_same_agree_however_they_are_written()
    {
        var night = new TimeWindow([.. Enum.GetValues<DayOfWeek>()], new(22, 0), new(6, 0), 45.00m);
        var whole = new InconvenientHoursRule("a", new(First), Clause, [night]);
        var split = new InconvenientHoursRule("x", new(First), new("§ 9, stk. 9", "andet"),
            [night with { To = new(0, 0) }, night with { From = new(0, 0) }]);

        Assert.Empty(Differs([whole], [split]));
        // One period read as two with the same figure, and 12,50 for 12,5, are the same reading.
        Assert.Empty(Differs([Holiday("h", First, null, 12.5m)], [Holiday("p", First, Second, 12.50m), Holiday("q", Second, null, 12.5m)]));
    }

    [Fact]
    public void A_rule_the_second_reading_gives_other_figures_for_on_any_of_its_days_is_flagged()
    {
        var first = new Rule[] { Holiday("early", First, Second, 12.5m), Holiday("late", Second, null, 13m) };

        // The second reading agrees until a protocol raises it from 1 September 2027.
        var second = new Rule[] { Holiday("a", First, Second, 12.5m), Holiday("b", Second, new(2027, 9, 1), 13m), Holiday("c", new(2027, 9, 1), null, 13.5m) };

        Assert.Equal(["late"], Differs(first, second));
        var problem = Assert.Single(SecondReading.Compare(first, second));
        Assert.Equal((SecondReading.Code, "A second reading of the agreement gives other figures for 2027-09-01."), (problem.Code, problem.Message));
    }

    [Fact]
    public void A_rule_on_days_the_second_reading_has_no_rule_for_is_flagged_and_a_rule_only_the_second_has_is_not()
    {
        var first = new Rule[] { Holiday("only-first", First, null, 12.5m) };

        Assert.Equal(["only-first"], Differs(first, [Holiday("a", Second, null, 12.5m)]));
        Assert.Equal(["only-first"], Differs(first, [new SpecialSavingsRule("s", new(First), Clause, 12.5m)]));
        // The second reading proposing more is not a fault in the first. Its extra rules are not added.
        Assert.Empty(Differs(first, [Holiday("a", First, null, 12.5m), new SpecialSavingsRule("s", new(First), Clause, 7m)]));
    }

    [Fact]
    public void Where_the_second_reading_holds_two_rules_for_a_day_one_that_agrees_is_enough()
    {
        var first = new Rule[] { Holiday("h", First, null, 12.5m) };

        Assert.Empty(Differs(first, [Holiday("a", First, null, 12.5m), Holiday("b", First, null, 13m)]));
    }

    [Fact]
    public void Apply_adds_the_problem_to_the_rule_and_keeps_the_ones_it_had()
    {
        var problem = new RuleProblem("h", "quote_not_in_clause", "...");
        var first = new ExtractionResult([new(Holiday("h", First, null, 12.5m), [], [problem]), new(new SpecialSavingsRule("s", new(First), Clause, 7m), [], [])], [], []);
        var second = new ExtractionResult([new(Holiday("a", First, null, 13m), [], []), new(new SpecialSavingsRule("t", new(First), Clause, 7m), [], [])], [], []);

        var applied = SecondReading.Apply(first, second);

        Assert.Equal(["quote_not_in_clause", SecondReading.Code], applied.Rules[0].Problems.Select(found => found.Code));
        Assert.Empty(applied.Rules[1].Problems);
    }
}

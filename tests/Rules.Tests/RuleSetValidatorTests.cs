namespace Overens.Rules.Tests;

public sealed class RuleSetValidatorTests
{
    private static readonly Period Always = new(Sample.Start);
    private static readonly Source Clause = new("§ 1", "tekst");

    [Fact]
    public void Accepts_the_sample_agreement_and_its_renewal()
    {
        Assert.Empty(RuleSetValidator.Validate(Sample.Agreement));
        Assert.Empty(RuleSetValidator.Validate(Sample.Renewed));
    }

    [Fact]
    public void Refuses_two_rules_of_a_kind_on_the_same_day()
    {
        var overlapping = new RuleSet("sample", 3,
        [
            new HolidayAllowanceRule("a", new(Sample.Start, new(2026, 6, 1)), Clause, 12.5m),
            new HolidayAllowanceRule("b", new(new(2026, 5, 31)), Clause, 12.5m),
        ]);

        Assert.Equal(["overlapping_period"], Codes(overlapping));
        Assert.Throws<InvalidOperationException>(() => overlapping.InForce(new(2026, 5, 31)));

        // A renewal that starts the day the old rule ends does not overlap.
        var adjoining = overlapping with
        {
            Rules = [overlapping.Rules[0], overlapping.Rules[1] with { InForce = new(new(2026, 6, 1)) }],
        };
        Assert.Empty(RuleSetValidator.Validate(adjoining));
    }

    public static TheoryData<string, Rule> BadRules => new()
    {
        { "no_source", new HolidayAllowanceRule("r", Always, new("§ 1", " "), 12.5m) },
        { "empty_period", new HolidayAllowanceRule("r", new(Sample.Start, Sample.Start), Clause, 12.5m) },
        { "percent_out_of_range", new HolidayAllowanceRule("r", Always, Clause, 125m) },
        { "percent_out_of_range", new SpecialSavingsRule("r", Always, Clause, -1m) },
        { "percent_out_of_range", new PensionRule("r", Always, Clause, 8, 104, 2) },
        { "negative_months", new PensionRule("r", Always, Clause, 8, 4, -2) },
        { "bands_do_not_start_at_zero", new MinimumWageRule("r", Always, Clause, [new(6, 140m)]) },
        { "bands_out_of_order", new MinimumWageRule("r", Always, Clause, [new(0, 140m), new(12, 150m), new(12, 155m)]) },
        { "rate_not_positive", new MinimumWageRule("r", Always, Clause, [new(0, 0m)]) },
        { "bad_daily_norm", new OvertimeRule("r", Always, Clause, 7.333m, [new(null, 50)], 100) },
        { "tiers_not_closed", new OvertimeRule("r", Always, Clause, 7.4m, [new(2, 50)], 100) },
        { "bad_tier_length", new OvertimeRule("r", Always, Clause, 7.4m, [new(0, 50), new(null, 100)], 100) },
        { "percent_out_of_range", new OvertimeRule("r", Always, Clause, 7.4m, [new(null, 5000)], 100) },
        { "no_windows", new InconvenientHoursRule("r", Always, Clause, []) },
        { "empty_window", new InconvenientHoursRule("r", Always, Clause, [new([], new(18, 0), new(22, 0), 30m)]) },
        { "rate_not_positive", new InconvenientHoursRule("r", Always, Clause, [new([DayOfWeek.Monday], new(18, 0), new(22, 0), 0m)]) },
    };

    [Theory]
    [MemberData(nameof(BadRules))]
    public void Names_what_is_wrong_with_a_rule(string code, Rule rule)
    {
        Assert.Equal([code], Codes(new("sample", 1, [rule])));
    }

    [Fact]
    public void Refuses_a_rule_id_used_twice()
    {
        var twice = new RuleSet("sample", 1,
        [
            new HolidayAllowanceRule("r", Always, Clause, 12.5m),
            new SpecialSavingsRule("r", Always, Clause, 7m),
        ]);

        Assert.Equal(["duplicate_id"], Codes(twice));
    }

    private static IEnumerable<string> Codes(RuleSet ruleSet) =>
        RuleSetValidator.Validate(ruleSet).Select(problem => problem.Code);
}

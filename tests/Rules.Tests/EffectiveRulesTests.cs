namespace Overens.Rules.Tests;

public sealed class EffectiveRulesTests
{
    private static readonly Source Clause = new("§ 1", "tekst");

    private static HolidayAllowanceRule Holiday(string id, int fromYear, int? untilYear, decimal percent) =>
        new(id, new(new(fromYear, 3, 1), untilYear is { } year ? new(year, 3, 1) : null), Clause, percent);

    private static PensionRule Pension(string id, int fromYear, decimal employer) =>
        new(id, new(new(fromYear, 3, 1)), Clause, employer, 4, 2);

    [Fact]
    public void A_renewal_takes_over_from_the_day_it_starts_and_leaves_the_earlier_years_alone()
    {
        var first = new RuleSet("lager", 1, [Holiday("holiday-2026", 2026, 2027, 12.5m), Holiday("holiday-2027", 2027, null, 12.7m), Pension("pension-2026", 2026, 8)]);
        var renewal = new RuleSet("lager", 2, [Holiday("holiday-2029", 2029, null, 13m), Pension("pension-2029", 2029, 9)]);

        var rules = EffectiveRules.From([renewal, first]);

        Assert.Empty(RuleSetValidator.Validate(rules));
        Assert.Equal(12.5m, rules.InForce(new(2026, 6, 1)).HolidayAllowance!.Percent);
        Assert.Equal(12.7m, rules.InForce(new(2029, 2, 28)).HolidayAllowance!.Percent);
        Assert.Equal(13m, rules.InForce(new(2029, 3, 1)).HolidayAllowance!.Percent);
        Assert.Equal(8, rules.InForce(new(2028, 1, 1)).Pension!.EmployerPercent);
        Assert.Equal(9, rules.InForce(new(2030, 1, 1)).Pension!.EmployerPercent);
        Assert.Equal(("lager", 2), (rules.AgreementId, rules.Version));
    }

    [Fact]
    public void A_corrected_upload_replaces_the_rules_it_restates_and_keeps_the_rest()
    {
        var first = new RuleSet("lager", 1, [Holiday("holiday-2026", 2026, null, 12.5m), Pension("pension-2026", 2026, 8)]);
        var corrected = new RuleSet("lager", 2, [Pension("pension-2026", 2026, 8.5m)]);

        var rules = EffectiveRules.From([first, corrected]);

        Assert.Empty(RuleSetValidator.Validate(rules));
        Assert.Equal(8.5m, rules.InForce(new(2026, 6, 1)).Pension!.EmployerPercent);
        Assert.Equal(12.5m, rules.InForce(new(2026, 6, 1)).HolidayAllowance!.Percent);
        Assert.Equal(2, rules.Rules.Count);
    }

    [Fact]
    public void One_version_is_its_own_rules_and_none_is_refused()
    {
        var only = new RuleSet("lager", 1, [Holiday("holiday-2026", 2026, null, 12.5m)]);

        Assert.Equal(only.Rules, EffectiveRules.From([only]).Rules);
        Assert.Throws<ArgumentException>(() => EffectiveRules.From([]));
    }

    [Fact]
    public void The_corpus_shape_of_a_renewal_gives_one_valid_rule_set_across_both_terms()
    {
        var joined = EffectiveRules.From([Sample.Agreement, Sample.Renewed with { Version = 2 }]);

        Assert.Empty(RuleSetValidator.Validate(joined));
        Assert.Equal(140.00m, joined.InForce(new(2026, 3, 10)).MinimumWage!.Bands[0].HourlyRate);
        Assert.Equal(152.00m, joined.InForce(new(2026, 3, 20)).MinimumWage!.Bands[0].HourlyRate);
    }
}

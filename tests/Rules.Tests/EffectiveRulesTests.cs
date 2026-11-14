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

public sealed class RuleChangesTests
{
    private static readonly Source Clause = new("§ 1", "tekst");

    private static HolidayAllowanceRule Holiday(int fromYear, int? untilYear, decimal percent, string clause = "§ 1") =>
        new($"holiday-{fromYear}", new(new(fromYear, 3, 1), untilYear is { } year ? new(year, 3, 1) : null), new(clause, "tekst"), percent);

    private static PensionRule Pension(int fromYear, decimal employer) => new($"pension-{fromYear}", new(new(fromYear, 3, 1)), Clause, employer, 4, 2);

    [Fact]
    public void A_first_rule_set_changes_everything_from_nothing()
    {
        var changes = RuleChanges.Between(null, new("lager", 1, [Holiday(2026, null, 12.5m), Pension(2026, 8)]));

        Assert.Equal(["pension", "holiday_allowance"], changes.Select(change => change.Kind));
        Assert.All(changes, change => Assert.Equal((new DateOnly(2026, 3, 1), null, null), (change.From, change.Until, change.Before)));
    }

    [Fact]
    public void A_renewal_changes_only_the_days_from_its_start()
    {
        var before = new RuleSet("lager", 1, [Holiday(2026, null, 12.5m), Pension(2026, 8)]);
        var after = EffectiveRules.From([before, new("lager", 2, [Holiday(2029, null, 13m), Pension(2029, 8)])]);

        var change = Assert.Single(RuleChanges.Between(before, after));

        // The pension is restated with the same figures, so nothing about it changes.
        Assert.Equal(("holiday_allowance", new DateOnly(2029, 3, 1), null), (change.Kind, change.From, change.Until));
        Assert.Equal((12.5m, 13m), (((HolidayAllowanceRule)change.Before!).Percent, ((HolidayAllowanceRule)change.After!).Percent));
    }

    [Fact]
    public void A_version_that_alters_a_past_period_or_leaves_days_without_a_rule_shows_it()
    {
        var before = new RuleSet("lager", 1, [Holiday(2026, 2027, 12.5m), Holiday(2027, null, 12.7m)]);
        // 2026 quietly lowered, and nothing from March 2028.
        var after = new RuleSet("lager", 2, [Holiday(2026, 2027, 12.0m), Holiday(2027, 2028, 12.7m)]);

        var changes = RuleChanges.Between(before, after);

        Assert.Equal(
            [(new DateOnly(2026, 3, 1), (DateOnly?)new DateOnly(2027, 3, 1), true), (new DateOnly(2028, 3, 1), null, false)],
            changes.Select(change => (change.From, change.Until, change.After is not null)));
    }

    [Fact]
    public void The_same_rules_quoted_from_other_clauses_under_other_names_are_no_change()
    {
        var before = new RuleSet("lager", 1, [Holiday(2026, 2027, 12.5m), Holiday(2027, null, 12.5m)]);
        var after = new RuleSet("lager", 2, [Holiday(2026, null, 12.5m, clause: "§ 9")]);

        Assert.Empty(RuleChanges.Between(before, after));
    }
}

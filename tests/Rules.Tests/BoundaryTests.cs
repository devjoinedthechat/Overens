namespace Overens.Rules.Tests;

/// <summary>
/// The edges: the last minute of a window, the wage exactly at the minimum, a percentage of
/// exactly 100. Each test here is one that mutation testing showed was missing: the code
/// could be changed at that edge and every other test still passed.
/// </summary>
public sealed class BoundaryTests
{
    private static readonly Period Always = new(Sample.Start);
    private static readonly Source Clause = new("§ 1", "tekst");
    private static readonly DateOnly Monday = new(2026, 3, 2);

    private static IEnumerable<string> Codes(params Rule[] rules) =>
        RuleSetValidator.Validate(new("sample", 1, rules)).Select(problem => problem.Code);

    [Fact]
    public void A_window_holds_its_first_minute_and_not_the_minute_it_ends()
    {
        var evening = new TimeWindow([DayOfWeek.Monday], new(18, 0), new(22, 0), 30m);
        var night = new TimeWindow([DayOfWeek.Monday], new(22, 0), new(6, 0), 45m);
        DateTime At(int hour, int minute) => Monday.ToDateTime(new(hour, minute));

        Assert.Equal([false, true, true, false], new[] { At(17, 59), At(18, 0), At(21, 59), At(22, 0) }.Select(evening.Contains));
        Assert.Equal([false, true, true, false], new[] { At(21, 59), At(22, 0), At(5, 59), At(6, 0) }.Select(night.Contains));
    }

    [Fact]
    public void An_overtime_tier_ends_with_its_last_minute()
    {
        var overtime = Sample.Agreement.Rules.OfType<OvertimeRule>().Single();

        Assert.Equal(
            (50m, 50m, 100m, 100m),
            (overtime.PercentAfter(0), overtime.PercentAfter(119), overtime.PercentAfter(120), overtime.PercentAfter(500)));
    }

    [Fact]
    public void A_long_day_passes_from_the_first_overtime_tier_into_the_second()
    {
        // Eleven hours against a norm of 7.5: two hours at 50 pct. and an hour and a half at 100 pct.
        var overtime = PayEngine.Expect(Sample.Agreement, Sample.Sheet(new Shift(Monday, new(8, 0), new(19, 0))), 150.00m).Components
            .Where(part => part.Line == PayLineKind.Overtime)
            .Select(part => (part.Minutes, part.Rate, part.Amount));

        Assert.Equal([(120, 75.00m, 150.00m), (90, 150.00m, 225.00m)], overtime);
    }

    [Fact]
    public void Periods_that_only_touch_do_not_overlap_whichever_is_asked()
    {
        var first = new Period(Sample.Start, new(2026, 3, 16));
        var second = new Period(new(2026, 3, 16));

        Assert.False(first.Overlaps(second));
        Assert.False(second.Overlaps(first));
        Assert.True(second.Overlaps(first with { Until = new(2026, 3, 17) }));
        Assert.True((first with { Until = new(2026, 3, 17) }).Overlaps(second));
    }

    [Fact]
    public void A_wage_of_nothing_and_the_highest_wage_are_worked_out_and_one_oere_beyond_is_refused()
    {
        Assert.NotEmpty(PayEngine.Expect(Sample.Agreement, Sample.March, 0m).Components);
        Assert.NotEmpty(PayEngine.Expect(Sample.Agreement, Sample.March, PayEngine.MaxHourlyRate).Components);
        Assert.Contains("between 0 and 100000", Assert.Throws<ArgumentException>(() => PayEngine.Expect(Sample.Agreement, Sample.March, -0.01m)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => PayEngine.Expect(Sample.Agreement, Sample.March, PayEngine.MaxHourlyRate + 0.01m));
    }

    [Fact]
    public void A_shift_on_the_periods_first_or_last_day_is_in_the_period()
    {
        Timesheet On(DateOnly day) => Sample.Sheet(new Shift(day, new(8, 0), new(12, 0)));

        Assert.NotEmpty(PayEngine.Expect(Sample.Agreement, On(new(2026, 3, 1)), 150.00m).Components);
        Assert.NotEmpty(PayEngine.Expect(Sample.Agreement, On(new(2026, 3, 31)), 150.00m).Components);
        Assert.Contains("2026-04-01 is outside the period", Assert.Throws<ArgumentException>(() => PayEngine.Expect(Sample.Agreement, On(new(2026, 4, 1)), 150.00m)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Work_on_the_day_of_hiring_is_work_after_hiring()
    {
        var hiredToday = new Employee("emp-3", Monday);

        Assert.NotEmpty(PayEngine.Expect(Sample.Agreement, Sample.Sheet(hiredToday, new Shift(Monday, new(8, 0), new(12, 0))), 150.00m).Components);
    }

    [Fact]
    public void A_wage_exactly_at_the_minimum_is_not_below_it()
    {
        // Thirteen months employed: the minimum is 148.50.
        Assert.Empty(PayEngine.Check(Sample.Agreement, Sample.March, Sample.Faithful(Sample.Agreement, Sample.March, 148.50m)).Findings);

        var below = PayEngine.Check(Sample.Agreement, Sample.March, Sample.Faithful(Sample.Agreement, Sample.March, 148.50m) with { HourlyRate = 148.49m });
        Assert.Equal([(FindingKind.RateBelowMinimum, 148.50m, 148.49m)], below.Findings.Select(finding => (finding.Kind, finding.Expected, finding.Actual)));
    }

    [Fact]
    public void A_line_that_pays_too_much_is_reported_as_too_high()
    {
        var faithful = Sample.Faithful(Sample.Agreement, Sample.March, 150.00m);
        var generous = faithful.With(PayLineKind.HolidayAllowance, 675.63m);

        var finding = Assert.Single(PayEngine.Check(Sample.Agreement, Sample.March, generous).Findings);

        Assert.Equal((FindingKind.TooHigh, PayLineKind.HolidayAllowance, 665.63m, 675.63m, 10.00m), (finding.Kind, finding.Line, finding.Expected, finding.Actual, finding.Difference));
    }

    [Fact]
    public void A_share_of_nothing_is_not_a_line()
    {
        // The employee pays no pension, and nothing goes to savings. Neither is a line of 0,00 kr.
        var rules = new RuleSet("sample", 1,
        [
            new PensionRule("pension", Always, Clause, 8, 0, 0),
            new SpecialSavingsRule("savings", Always, Clause, 0),
        ]);

        var lines = PayEngine.Expect(rules, Sample.March, 150.00m).Components.Select(part => part.Line);

        Assert.Equal([PayLineKind.BasePay, PayLineKind.PensionEmployer], lines);
    }

    [Fact]
    public void The_parts_come_in_one_order_by_line_rule_rate_and_what_they_are_a_share_of()
    {
        // Hired so that pension starts on 10 March, with work on both sides of it and of the renewal on 16 March.
        var employee = new Employee("emp-2", new(2026, 1, 10));
        var timesheet = Sample.Sheet(
            employee,
            new Shift(new(2026, 3, 3), new(8, 0), new(19, 0)),
            new Shift(new(2026, 3, 12), new(14, 0), new(23, 0)),
            new Shift(new(2026, 3, 20), new(8, 0), new(19, 0)));

        var parts = PayEngine.Expect(Sample.Renewed, timesheet, 170.00m).Components;

        Assert.Equal(
            parts.OrderBy(part => part.Line).ThenBy(part => part.RuleId, StringComparer.Ordinal).ThenBy(part => part.Rate).ThenBy(part => part.Basis),
            parts);
        // The data does tell the orders apart: two rules on a line, two rates under a rule, two amounts a rate is taken of.
        Assert.Equal(["overtime", "overtime-2"], parts.Where(part => part.Line == PayLineKind.Overtime).Select(part => part.RuleId).Distinct());
        Assert.Equal([85.00m, 170.00m], parts.Where(part => part.RuleId == "overtime").Select(part => part.Rate).Distinct());
        var holiday = parts.Where(part => part.RuleId == "holiday").Select(part => part.Basis).ToList();
        Assert.Equal(2, holiday.Count);
        Assert.True(holiday[0] < holiday[1]);
    }

    [Fact]
    public void A_wage_below_two_minimums_is_reported_for_the_earlier_one_first()
    {
        var timesheet = Sample.Sheet(
            new Shift(new(2026, 3, 20), new(8, 0), new(12, 0)),
            new Shift(new(2026, 3, 3), new(8, 0), new(12, 0)));

        var findings = PayEngine.Check(Sample.Renewed, timesheet, Sample.Faithful(Sample.Renewed, timesheet, 170.00m) with { HourlyRate = 100.00m }).Findings
            .Where(finding => finding.Kind == FindingKind.RateBelowMinimum);

        Assert.Equal([("wage", 148.50m), ("wage-2", 160.00m)], findings.Select(finding => (finding.Rules.Single().Id, finding.Expected)));
    }

    public static TheoryData<Rule> AtTheLimit => new()
    {
        new HolidayAllowanceRule("r", Always, Clause, 0m),
        new HolidayAllowanceRule("r", Always, Clause, 100m),
        new SpecialSavingsRule("r", Always, Clause, 100m),
        new PensionRule("r", Always, Clause, 100m, 0m, 0),
        new OvertimeRule("r", Always, Clause, 24m, [new(null, 0m)], 300m),
        new OvertimeRule("r", Always, Clause, 7.4m, [new(2, 300m), new(null, 0m)], 0m),
    };

    [Theory]
    [MemberData(nameof(AtTheLimit))]
    public void A_value_exactly_at_a_limit_is_accepted(Rule rule)
    {
        Assert.Empty(Codes(rule));
    }

    public static TheoryData<string, Rule> JustOutside => new()
    {
        { "percent_out_of_range", new HolidayAllowanceRule("r", Always, Clause, 100.01m) },
        { "percent_out_of_range", new HolidayAllowanceRule("r", Always, Clause, -0.01m) },
        { "percent_out_of_range", new PensionRule("r", Always, Clause, 8m, 100.01m, 0) },
        { "percent_out_of_range", new OvertimeRule("r", Always, Clause, 7.4m, [new(null, 300.01m)], 100m) },
        { "percent_out_of_range", new OvertimeRule("r", Always, Clause, 7.4m, [new(null, 50m)], -0.01m) },
        // One bad tier among good ones, and one bad band among good ones.
        { "percent_out_of_range", new OvertimeRule("r", Always, Clause, 7.4m, [new(2, 50m), new(null, 500m)], 100m) },
        { "rate_not_positive", new MinimumWageRule("r", Always, Clause, [new(0, 140m), new(12, 0m)]) },
        { "bad_daily_norm", new OvertimeRule("r", Always, Clause, 0m, [new(null, 50m)], 100m) },
        { "bad_daily_norm", new OvertimeRule("r", Always, Clause, 24.5m, [new(null, 50m)], 100m) },
    };

    [Theory]
    [MemberData(nameof(JustOutside))]
    public void A_value_just_outside_a_limit_is_refused(string code, Rule rule)
    {
        Assert.Equal([code], Codes(rule));
    }

    [Fact]
    public void An_overlap_is_held_against_the_later_rule_however_the_rules_are_listed()
    {
        var earlier = new HolidayAllowanceRule("earlier", new(Sample.Start, new(2026, 6, 1)), Clause, 12.5m);
        var later = new HolidayAllowanceRule("later", new(new(2026, 5, 1)), Clause, 12.5m);

        foreach (var rules in new Rule[][] { [earlier, later], [later, earlier] })
        {
            var problem = Assert.Single(RuleSetValidator.Validate(new("sample", 1, rules)));
            Assert.Equal(("later", "overlapping_period"), (problem.RuleId, problem.Code));
            Assert.Contains("the same days as earlier", problem.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_problem_says_in_words_what_is_wrong()
    {
        var problems = RuleSetValidatorTests.BadRules
            .SelectMany(bad => RuleSetValidator.Validate(new("sample", 1, [(Rule)bad[1], (Rule)bad[1]])))
            .ToList();

        // Each bad rule twice: also an id used twice, and an overlap.
        Assert.Contains(problems, problem => problem.Code == "duplicate_id");
        Assert.All(problems, problem => Assert.Matches(@"^\p{Lu}.{15,}\.$", problem.Message));
    }

    [Fact]
    public void Rules_with_the_same_id_in_two_versions_are_told_apart_and_the_others_keep_their_ids()
    {
        var first = new RuleSet("lager", 1,
        [
            new HolidayAllowanceRule("holiday", new(new(2026, 3, 1)), Clause, 12.5m),
            new PensionRule("pension", new(new(2026, 3, 1)), Clause, 8, 4, 2),
        ]);
        var renewal = new RuleSet("lager", 2, [new HolidayAllowanceRule("holiday", new(new(2029, 3, 1)), Clause, 13m)]);

        var rules = EffectiveRules.From([first, renewal]);

        Assert.Equal(["holiday@20260301", "pension", "holiday@20290301"], rules.Rules.Select(rule => rule.Id));
        Assert.Contains("no published rule set", Assert.Throws<ArgumentException>(() => EffectiveRules.From([])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_change_that_carries_on_past_a_step_is_one_change_and_one_that_alters_there_is_two()
    {
        HolidayAllowanceRule Holiday(int from, int? until, decimal percent) =>
            new($"holiday-{from}", new(new(from, 3, 1), until is { } year ? new(year, 3, 1) : null), Clause, percent);
        var before = new RuleSet("lager", 1, [Holiday(2026, null, 12.5m)]);

        // Raised to 13 for good, written as two periods: one change, with no end.
        var same = Assert.Single(RuleChanges.Between(before, new("lager", 2, [Holiday(2026, 2027, 13m), Holiday(2027, null, 13m)])));
        Assert.Equal((new DateOnly(2026, 3, 1), null), (same.From, same.Until));

        // Raised to 13 and then to 13.5: two changes.
        var stepped = RuleChanges.Between(before, new("lager", 2, [Holiday(2026, 2027, 13m), Holiday(2027, null, 13.5m)]));
        Assert.Equal(
            [(new DateOnly(2026, 3, 1), (DateOnly?)new DateOnly(2027, 3, 1), 13m), (new DateOnly(2027, 3, 1), null, 13.5m)],
            stepped.Select(change => (change.From, change.Until, ((HolidayAllowanceRule)change.After!).Percent)));

        // Raised only from 2028, with a period in between that says what was said before: one change, from 2028.
        var later = Assert.Single(RuleChanges.Between(before, new("lager", 2, [Holiday(2026, 2027, 12.5m), Holiday(2027, 2028, 12.5m), Holiday(2028, null, 13m)])));
        Assert.Equal((new DateOnly(2028, 3, 1), null), (later.From, later.Until));
    }

    [Fact]
    public void Every_kind_of_rule_is_named_in_what_a_first_version_changes()
    {
        Assert.Equal(
            ["minimum_wage", "overtime", "inconvenient_hours", "pension", "holiday_allowance", "special_savings"],
            RuleChanges.Between(null, Sample.Agreement).Select(change => change.Kind));
    }

    [Fact]
    public void Two_rules_on_one_day_are_refused_by_name()
    {
        var twice = new RuleSet("sample", 1,
        [
            new HolidayAllowanceRule("a", Always, Clause, 12.5m),
            new HolidayAllowanceRule("b", Always, Clause, 13m),
        ]);

        Assert.Contains("Rules a and b both apply on 2026-03-02", Assert.Throws<InvalidOperationException>(() => twice.InForce(Monday)).Message, StringComparison.Ordinal);
    }
}

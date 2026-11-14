namespace Overens.Rules.Tests;

public sealed class PayEngineTests
{
    [Fact]
    public void Works_out_a_month_by_the_rules_of_the_agreement()
    {
        var check = PayEngine.Check(Sample.Agreement, Sample.March, Sample.Faithful(Sample.Agreement, Sample.March, 150.00m));

        // 8 + 9 + 8 hours at 150.00.
        Assert.Equal(3750.00m, check.ExpectedFor(PayLineKind.BasePay));
        // Half an hour and one and a half hours beyond 7.5 a day at 50%, and six Sunday hours at 100%.
        Assert.Equal(150.00m + 900.00m, check.ExpectedFor(PayLineKind.Overtime));
        // Four weekday evening hours at 30.00, and nine night hours at 45.00.
        Assert.Equal(120.00m + 405.00m, check.ExpectedFor(PayLineKind.InconvenientHours));
        // Percentages of the 5,325.00 above.
        Assert.Equal(426.00m, check.ExpectedFor(PayLineKind.PensionEmployer));
        Assert.Equal(213.00m, check.ExpectedFor(PayLineKind.PensionEmployee));
        Assert.Equal(665.63m, check.ExpectedFor(PayLineKind.HolidayAllowance));
        Assert.Equal(372.75m, check.ExpectedFor(PayLineKind.SpecialSavings));
        Assert.Empty(check.Findings);
    }

    [Fact]
    public void Shows_how_each_line_was_reached()
    {
        var overtime = PayEngine.Expect(Sample.Agreement, Sample.March, 150.00m).Components
            .Where(part => part.Line == PayLineKind.Overtime)
            .Select(part => (part.RuleId, part.Minutes, part.Rate, part.Amount));

        Assert.Equal([("overtime", 120, 75.00m, 150.00m), ("overtime", 360, 150.00m, 900.00m)], overtime);
    }

    [Fact]
    public void Counts_the_daily_norm_across_every_shift_that_starts_that_day()
    {
        // Four hours, a lunch break, then four and a half: one hour beyond the 7.5-hour norm.
        var timesheet = Sample.Sheet(
            new Shift(new(2026, 3, 2), new(8, 0), new(12, 0)),
            new Shift(new(2026, 3, 2), new(13, 0), new(17, 30)));

        var overtime = Assert.Single(
            PayEngine.Expect(Sample.Agreement, timesheet, 150.00m).Components,
            part => part.Line == PayLineKind.Overtime);

        Assert.Equal((60, 75.00m), (overtime.Minutes, overtime.Amount));
    }

    [Fact]
    public void Multiplies_once_so_that_a_half_oere_is_not_lost_to_repeating_decimals()
    {
        // Three minutes at 30.50 an hour is exactly 1.525, which rounds to 1.53. Adding up
        // three times 0.508333... would give 1.52499... and round to 1.52.
        var rules = new RuleSet("odd", 1,
        [
            new InconvenientHoursRule("evening", new(Sample.Start), new("§ 10", "forskudt tid"),
                [new([DayOfWeek.Monday], new(18, 0), new(22, 0), 30.50m)]),
        ]);
        var timesheet = Sample.Sheet(new Shift(new(2026, 3, 2), new(18, 0), new(18, 3)));

        var supplement = Assert.Single(
            PayEngine.Expect(rules, timesheet, 150.00m).Components,
            part => part.Line == PayLineKind.InconvenientHours);

        Assert.Equal(1.53m, supplement.Amount);
    }

    [Fact]
    public void Reports_a_line_that_pays_too_little_with_the_rule_behind_it()
    {
        var payslip = Sample.Faithful(Sample.Agreement, Sample.March, 150.00m).With(PayLineKind.Overtime, 150.00m);

        var finding = Assert.Single(PayEngine.Check(Sample.Agreement, Sample.March, payslip).Findings);

        Assert.Equal(FindingKind.TooLow, finding.Kind);
        Assert.Equal(PayLineKind.Overtime, finding.Line);
        Assert.Equal(1050.00m, finding.Expected);
        Assert.Equal(-900.00m, finding.Difference);
        Assert.Equal("§ 9, stk. 2", Assert.Single(finding.Rules).Source.ClauseId);
    }

    [Fact]
    public void Reports_a_missing_line_and_a_line_the_agreement_does_not_call_for()
    {
        var onlyNormalHours = Sample.Sheet(new Shift(new(2026, 3, 2), new(8, 0), new(15, 0)));
        var payslip = Sample.Faithful(Sample.Agreement, onlyNormalHours, 150.00m)
            .Without(PayLineKind.SpecialSavings)
            .With(PayLineKind.Overtime, 75.00m);

        var findings = PayEngine.Check(Sample.Agreement, onlyNormalHours, payslip).Findings;

        Assert.Equal(
            [(FindingKind.UnexpectedLine, PayLineKind.Overtime), (FindingKind.MissingLine, PayLineKind.SpecialSavings)],
            findings.Select(finding => (finding.Kind, finding.Line!.Value)));
    }

    [Fact]
    public void Holds_a_wage_below_the_minimum_to_the_minimum_for_the_employees_seniority()
    {
        var shift = new Shift(new(2026, 3, 2), new(8, 0), new(12, 0));
        var newHire = Sample.Sheet(new Employee("emp-2", new(2026, 1, 5)), shift);
        var paidAtOwnRate = Sample.Faithful(new("none", 1, []), newHire, 139.00m);

        var findings = PayEngine.Check(Sample.Agreement, newHire, paidAtOwnRate).Findings;

        var rate = Assert.Single(findings, finding => finding.Kind == FindingKind.RateBelowMinimum);
        Assert.Equal((140.00m, 139.00m), (rate.Expected, rate.Actual));
        var basePay = Assert.Single(findings, finding => finding.Line == PayLineKind.BasePay);
        Assert.Equal((560.00m, 556.00m), (basePay.Expected, basePay.Actual));

        // After twelve months the higher band applies, and 145.00 is no longer enough.
        var experienced = PayEngine.Expect(Sample.Agreement, Sample.Sheet(shift), 145.00m);
        Assert.Equal(148.50m, Assert.Single(experienced.Shortfalls).Value);
    }

    [Fact]
    public void Pays_no_pension_before_the_qualifying_months_are_served()
    {
        var hiredInFebruary = new Employee("emp-3", new(2026, 2, 10));
        var timesheet = Sample.Sheet(
            hiredInFebruary,
            new Shift(new(2026, 3, 9), new(8, 0), new(12, 0)),
            new Shift(new(2026, 4, 10), new(8, 0), new(12, 0))) with
        { PeriodEnd = new(2026, 4, 30) };

        var expected = PayEngine.Expect(Sample.Agreement, timesheet, 150.00m).Components;

        // Only the April shift, two full months after hiring, carries pension.
        var pension = Assert.Single(expected, part => part.Line == PayLineKind.PensionEmployer);
        Assert.Equal((600.00m, 48.00m), (pension.Basis, pension.Amount));
    }

    [Fact]
    public void Pays_each_shift_by_the_rules_in_force_on_the_day_it_starts()
    {
        var timesheet = Sample.Sheet(
            new Shift(new(2026, 3, 13), new(18, 0), new(20, 0)),
            new Shift(new(2026, 3, 16), new(18, 0), new(20, 0)));

        var expected = PayEngine.Expect(Sample.Renewed, timesheet, 150.00m);

        // Friday is under the old rules. Monday is under the renewal: a higher minimum,
        // a higher evening supplement and a higher employer pension.
        Assert.Equal(
            [(null, 150.00m, 300.00m), ("wage-2", 160.00m, 320.00m)],
            expected.Components.Where(part => part.Line == PayLineKind.BasePay).Select(part => (part.RuleId, part.Rate, part.Amount)));
        Assert.Equal(
            [("inconvenient", 60.00m), ("inconvenient-2", 65.00m)],
            expected.Components.Where(part => part.Line == PayLineKind.InconvenientHours).Select(part => (part.RuleId!, part.Amount)));
        Assert.Equal(
            [("pension", 28.80m), ("pension-2", 34.65m)],
            expected.Components.Where(part => part.Line == PayLineKind.PensionEmployer).Select(part => (part.RuleId!, part.Amount)));
    }

    [Fact]
    public void Expects_only_base_pay_where_the_agreement_is_not_in_force()
    {
        var february = new Timesheet(Sample.Employee, new(2026, 2, 1), new(2026, 2, 28),
            [new(new(2026, 2, 3), new(14, 0), new(23, 30))]);

        var expected = PayEngine.Expect(Sample.Agreement, february, 100.00m).Components;

        Assert.Equal((PayLineKind.BasePay, 950.00m), Assert.Single(expected.Select(part => (part.Line, part.Amount))));
    }

    [Fact]
    public void Refuses_a_payslip_for_another_employee_or_period_and_a_shift_outside_the_period()
    {
        var payslip = Sample.Faithful(Sample.Agreement, Sample.March, 150.00m);

        Assert.Throws<ArgumentException>(() => PayEngine.Check(Sample.Agreement, Sample.March, payslip with { EmployeeId = "emp-9" }));
        Assert.Throws<ArgumentException>(() => PayEngine.Check(Sample.Agreement, Sample.March, payslip with { PeriodEnd = new(2026, 3, 30) }));
        Assert.Throws<ArgumentException>(() =>
            PayEngine.Expect(Sample.Agreement, Sample.Sheet(new Shift(new(2026, 4, 1), new(8, 0), new(9, 0))), 150.00m));
    }
}

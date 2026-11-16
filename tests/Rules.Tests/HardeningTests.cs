namespace Overens.Rules.Tests;

public sealed class HardeningTests
{
    [Theory]
    // Hired on the 31st: a month with no 31st is full on its last day.
    [InlineData("2025-08-31", "2026-02-27", 5)]
    [InlineData("2025-08-31", "2026-02-28", 6)]
    [InlineData("2025-08-31", "2026-03-30", 6)]
    [InlineData("2025-08-31", "2026-03-31", 7)]
    // Hired on the 30th of January, in a leap year and not.
    [InlineData("2027-01-30", "2027-02-28", 1)]
    [InlineData("2028-01-30", "2028-02-28", 0)]
    [InlineData("2028-01-30", "2028-02-29", 1)]
    // An ordinary day of the month, and a day before the hiring.
    [InlineData("2025-01-15", "2025-07-14", 5)]
    [InlineData("2025-01-15", "2025-07-15", 6)]
    [InlineData("2025-01-15", "2024-12-01", 0)]
    public void Months_of_employment_are_full_on_the_day_hired_or_the_months_last_day(string hired, string on, int months)
    {
        Assert.Equal(months, new Employee("emp-1", DateOnly.Parse(hired, System.Globalization.CultureInfo.InvariantCulture)).MonthsEmployedOn(DateOnly.Parse(on, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Time_worked_twice_is_refused_and_shifts_that_only_touch_are_not()
    {
        var day = new DateOnly(2026, 3, 2);
        var twice = Sample.Sheet(new Shift(day, new(8, 0), new(16, 0)), new Shift(day, new(8, 0), new(16, 0)));
        var into = Sample.Sheet(new Shift(day, new(8, 0), new(16, 0)), new Shift(day, new(15, 59), new(18, 0)));
        // A night shift that ends at 06:00 the next day, and a shift that starts at 05:00 that day.
        var overMidnight = Sample.Sheet(new Shift(day, new(22, 0), new(6, 0)), new Shift(day.AddDays(1), new(5, 0), new(9, 0)));
        var touching = Sample.Sheet(new Shift(day, new(8, 0), new(16, 0)), new Shift(day, new(16, 0), new(18, 0)), new Shift(day, new(22, 0), new(6, 0)), new Shift(day.AddDays(1), new(6, 0), new(9, 0)));

        foreach (var sheet in new[] { twice, into, overMidnight })
        {
            Assert.Contains("overlaps", Assert.Throws<ArgumentException>(() => PayEngine.Expect(Sample.Agreement, sheet, 150m)).Message, StringComparison.Ordinal);
        }

        Assert.NotEmpty(PayEngine.Expect(Sample.Agreement, touching, 150m).Components);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100_000.01)]
    [InlineData(1e27)]
    public void An_hourly_wage_no_payslip_could_hold_is_refused_before_any_arithmetic(double rate)
    {
        Assert.Throws<ArgumentException>(() => PayEngine.Expect(Sample.Agreement, Sample.March, (decimal)rate));
    }

    [Fact]
    public void A_line_no_rule_covers_is_reported_as_not_checked_and_not_as_wrong()
    {
        var rate = 150m;
        var faithful = Sample.Faithful(Sample.Agreement, Sample.March, rate);
        var noSupplements = new RuleSet("sample", 1, [.. Sample.Agreement.Rules.Where(rule => rule is not InconvenientHoursRule)]);

        var check = PayEngine.Check(noSupplements, Sample.March, faithful);

        // The payslip is right. Without the rule that cannot be known, and nothing says it is wrong:
        // not the supplement, and not the percentages that are taken of pay with the supplement in it.
        PayLineKind[] unknown = [PayLineKind.InconvenientHours, PayLineKind.PensionEmployer, PayLineKind.PensionEmployee, PayLineKind.HolidayAllowance, PayLineKind.SpecialSavings];
        Assert.Equal(unknown, check.Unchecked);
        Assert.All(check.Findings, finding => Assert.Equal((FindingKind.NotCovered, 0m), (finding.Kind, finding.Difference)));
        Assert.Equal(unknown, check.Findings.Select(finding => finding.Line!.Value));

        // A missing percentage rule leaves only its own line unchecked.
        var noSavings = PayEngine.Check(new("sample", 1, [.. Sample.Agreement.Rules.Where(rule => rule is not SpecialSavingsRule)]), Sample.March, faithful);
        Assert.Equal([PayLineKind.SpecialSavings], noSavings.Unchecked);
        Assert.Equal([(FindingKind.NotCovered, (PayLineKind?)PayLineKind.SpecialSavings)], noSavings.Findings.Select(finding => (finding.Kind, finding.Line)));

        // A payslip that leaves the line out raises nothing about it, and the line is still listed as not checked.
        var leftOut = faithful with { Lines = [.. faithful.Lines.Where(line => line.Kind != PayLineKind.InconvenientHours)] };
        var silent = PayEngine.Check(noSupplements, Sample.March, leftOut);
        Assert.DoesNotContain(silent.Findings, finding => finding.Line == PayLineKind.InconvenientHours);
        Assert.Equal(unknown, silent.Unchecked);

        // With every rule in place nothing is unchecked, and a line the rules do not grant is still unexpected.
        Assert.Empty(PayEngine.Check(Sample.Agreement, Sample.March, faithful).Unchecked);
    }

    [Fact]
    public void Work_dated_before_the_employee_was_hired_is_refused()
    {
        var early = Sample.Sheet(new Employee("emp-1", new(2026, 3, 10)), new Shift(new(2026, 3, 2), new(8, 0), new(16, 0)));

        Assert.Contains("before the employee was hired", Assert.Throws<ArgumentException>(() => PayEngine.Expect(Sample.Agreement, early, 150m)).Message, StringComparison.Ordinal);
    }
}

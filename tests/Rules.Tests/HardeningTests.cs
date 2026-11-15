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
}

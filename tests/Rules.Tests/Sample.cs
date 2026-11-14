namespace Overens.Rules.Tests;

/// <summary>A small agreement, written by hand, with numbers that are easy to check on paper.</summary>
public static class Sample
{
    public static readonly DateOnly Start = new(2026, 3, 1);

    private static readonly DayOfWeek[] Weekdays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    private static readonly DayOfWeek[] EveryDay = [.. Enum.GetValues<DayOfWeek>()];

    public static RuleSet Agreement { get; } = new("sample", 1, Rules(new Period(Start), "", 140.00m, 148.50m, 30.00m, 8m));

    /// <summary>The same agreement, renewed on 16 March with higher rates.</summary>
    public static RuleSet Renewed { get; } = new("sample", 2,
    [
        .. Rules(new Period(Start, new(2026, 3, 16)), "", 140.00m, 148.50m, 30.00m, 8m),
        .. Rules(new Period(new(2026, 3, 16)), "-2", 152.00m, 160.00m, 32.50m, 9m),
    ]);

    public static Employee Employee { get; } = new("emp-1", new(2025, 1, 15));

    /// <summary>
    /// Monday 2 March 08:00 to 16:00, Tuesday 3 March 14:00 to 23:00, and Saturday 7 March
    /// 22:00 to 06:00 on Sunday.
    /// </summary>
    public static Timesheet March { get; } = Sheet(
        new Shift(new(2026, 3, 2), new(8, 0), new(16, 0)),
        new Shift(new(2026, 3, 3), new(14, 0), new(23, 0)),
        new Shift(new(2026, 3, 7), new(22, 0), new(6, 0)));

    public static Timesheet Sheet(params Shift[] shifts) => Sheet(Employee, shifts);

    public static Timesheet Sheet(Employee employee, params Shift[] shifts) =>
        new(employee, Start, new(2026, 3, 31), shifts);

    /// <summary>A payslip that pays exactly what the rule set requires.</summary>
    public static Payslip Faithful(RuleSet rules, Timesheet timesheet, decimal rate)
    {
        var expected = PayEngine.Expect(rules, timesheet, rate).Components;
        return new(
            timesheet.Employee.Id,
            timesheet.PeriodStart,
            timesheet.PeriodEnd,
            rate,
            [.. expected.GroupBy(part => part.Line).Select(line => new PayLine(line.Key, line.Sum(part => part.Amount)))]);
    }

    public static Payslip With(this Payslip payslip, PayLineKind line, decimal amount) =>
        payslip with { Lines = [.. payslip.Lines.Where(paid => paid.Kind != line), new(line, amount)] };

    public static Payslip Without(this Payslip payslip, PayLineKind line) =>
        payslip with { Lines = [.. payslip.Lines.Where(paid => paid.Kind != line)] };

    private static Rule[] Rules(Period period, string suffix, decimal starter, decimal experienced, decimal evening, decimal employerPension) =>
    [
        new MinimumWageRule("wage" + suffix, period, new("§ 4, stk. 1", "Mindstebetalingen udgør"),
            [new(0, starter), new(12, experienced)]),
        new OvertimeRule("overtime" + suffix, period, new("§ 9, stk. 2", "For overarbejde betales"),
            7.5m, [new(2, 50), new(null, 100)], 100),
        new InconvenientHoursRule("inconvenient" + suffix, period, new("§ 10, stk. 1", "For arbejde på forskudt tid"),
            [new(Weekdays, new(18, 0), new(22, 0), evening), new(EveryDay, new(22, 0), new(6, 0), 45.00m)]),
        new PensionRule("pension" + suffix, period, new("§ 14, stk. 1", "Pensionsbidraget udgør"), employerPension, 4, 2),
        new HolidayAllowanceRule("holiday" + suffix, period, new("§ 16, stk. 1", "Feriegodtgørelsen udgør"), 12.5m),
        new SpecialSavingsRule("savings" + suffix, period, new("§ 17, stk. 1", "Til fritvalgskontoen indbetales"), 7),
    ];
}

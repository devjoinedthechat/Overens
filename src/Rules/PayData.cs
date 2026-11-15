namespace Overens.Rules;

/// <summary>An employee, known here only by an id that says nothing about the person.</summary>
public sealed record Employee(string Id, DateOnly HiredOn)
{
    /// <summary>
    /// Full months of employment on a day. A month is full on the day of the month the
    /// employee was hired, or on the month's last day when it has no such day: hired on
    /// 31 August, six months are full on 28 February.
    /// </summary>
    public int MonthsEmployedOn(DateOnly day)
    {
        var months = ((day.Year - HiredOn.Year) * 12) + day.Month - HiredOn.Month;
        var reached = day.Day >= HiredOn.Day || day.Day == DateTime.DaysInMonth(day.Year, day.Month);
        return Math.Max(0, reached ? months : months - 1);
    }
}

/// <summary>
/// Time worked without a break. An end that is not after the start is on the next day.
/// A break is the gap between two shifts.
/// </summary>
public sealed record Shift(DateOnly Date, TimeOnly Start, TimeOnly End)
{
    public int Minutes => (int)(End - Start).TotalMinutes;
}

/// <summary>What an employee worked in a pay period.</summary>
public sealed record Timesheet(Employee Employee, DateOnly PeriodStart, DateOnly PeriodEnd, IReadOnlyList<Shift> Shifts);

public enum PayLineKind
{
    /// <summary>Every hour worked, at the hourly wage.</summary>
    BasePay,

    /// <summary>The overtime supplement only. The hours themselves are in base pay.</summary>
    Overtime,
    InconvenientHours,
    PensionEmployer,
    PensionEmployee,
    HolidayAllowance,
    SpecialSavings,
}

public sealed record PayLine(PayLineKind Kind, decimal Amount);

/// <summary>What the employer says it paid for a pay period.</summary>
public sealed record Payslip(
    string EmployeeId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal HourlyRate,
    IReadOnlyList<PayLine> Lines);

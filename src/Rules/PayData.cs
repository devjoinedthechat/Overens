namespace Overens.Rules;

/// <summary>An employee, known here only by an id that says nothing about the person.</summary>
public sealed record Employee(string Id, DateOnly HiredOn)
{
    /// <summary>Full months of employment on a day.</summary>
    public int MonthsEmployedOn(DateOnly day)
    {
        var months = ((day.Year - HiredOn.Year) * 12) + day.Month - HiredOn.Month;
        return Math.Max(0, day.Day < HiredOn.Day ? months - 1 : months);
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

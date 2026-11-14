using Overens.Rules;

namespace Overens.Synthetic;

/// <summary>One employee's month: what they worked and what the payslip says they were paid.</summary>
public sealed record PayCase(Timesheet Timesheet, Payslip Payslip);

/// <summary>A pay line the payroll got wrong, with what it should have been.</summary>
public sealed record WrongLine(PayLineKind Line, decimal Expected, decimal Actual);

/// <summary>
/// The answer for one pay case: the mistake the payroll made, and every line it left wrong.
/// A case with no entry here was paid correctly.
/// </summary>
public sealed record SeededError(
    string EmployeeId,
    DateOnly PeriodStart,
    string Mistake,
    bool RateBelowMinimum,
    IReadOnlyList<WrongLine> Lines);

public sealed record PayData(IReadOnlyList<PayCase> Cases, IReadOnlyList<SeededError> Errors);

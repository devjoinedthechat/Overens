namespace Overens.Rules;

/// <summary>
/// One part of what a pay line should be, and how it was reached: minutes at an hourly rate,
/// or a percentage of an amount. <see cref="RuleId"/> is null for plain hours at the
/// employee's own wage.
/// </summary>
public sealed record Component(
    PayLineKind Line,
    string? RuleId,
    int? Minutes,
    decimal? Basis,
    decimal Rate,
    decimal Amount);

public enum FindingKind
{
    TooLow,
    TooHigh,
    MissingLine,
    UnexpectedLine,
    RateBelowMinimum,

    /// <summary>
    /// The payslip has a line that no approved rule covers in the period. It is not known
    /// to be wrong: it was not checked. This is not the same as a line the rules say should
    /// not be there.
    /// </summary>
    NotCovered,
}

/// <summary>
/// A difference between the payslip and the agreement. <see cref="Line"/> is null when the
/// finding is about the hourly rate itself. <see cref="Difference"/> is actual minus expected.
/// </summary>
public sealed record Finding(
    FindingKind Kind,
    PayLineKind? Line,
    decimal Expected,
    decimal Actual,
    IReadOnlyList<Rule> Rules)
{
    public decimal Difference => Actual - Expected;
}

/// <summary>What the pay should have been, and where the payslip differs.</summary>
/// <param name="Expected">What each line should be, part by part.</param>
/// <param name="Findings">Where the payslip differs, and lines that could not be checked.</param>
/// <param name="Unchecked">
/// The lines no approved rule covers on any day of the period. Whether the payslip has such
/// a line or leaves it out, nothing was checked: a missing rule is not a rule that says nothing is owed.
/// </param>
public sealed record PayCheck(IReadOnlyList<Component> Expected, IReadOnlyList<Finding> Findings, IReadOnlyList<PayLineKind> Unchecked)
{
    public decimal ExpectedFor(PayLineKind line) => Expected.Where(part => part.Line == line).Sum(part => part.Amount);
}

using Overens.Rules;

namespace Overens.Ingestion.Extraction;

/// <summary>
/// A rule the model read out of an agreement, before a person has approved it.
/// <see cref="AlsoFrom"/> names other clauses the rule draws on, such as the clause that
/// states the daily norm an overtime rule refers to. <see cref="Problems"/> is what checking
/// it without a model found; a rule with any problem cannot be approved as it stands.
/// </summary>
public sealed record ProposedRule(Rule Rule, IReadOnlyList<string> AlsoFrom, IReadOnlyList<RuleProblem> Problems);

/// <summary>
/// What the model reported for figures the code then worked out. The model never does the
/// sum: it gives the figure as the agreement prints it, and that figure, not the result, is
/// what must be printed where the rule cites. <see cref="None"/> is a rule given as final figures.
/// </summary>
/// <param name="Increase">The amount added per hour to every band of the period before.</param>
/// <param name="EmployeeShare">The employee's pension share as a part of the employer's: "half".</param>
/// <param name="Steps">Per seniority band, the amount added to the band before it, or null where the band's rate is printed.</param>
/// <param name="TotalPercent">A pension given only as a total, which the employer's part is taken of.</param>
/// <param name="EmployerShare">The employer's part of <paramref name="TotalPercent"/>; the employee pays the rest.</param>
/// <param name="EmployerPoints">The change in percentage points to the employer's pension share of the period before.</param>
/// <param name="EmployeePoints">The change in percentage points to the employee's pension share of the period before.</param>
/// <param name="Points">The change in percentage points to a holiday allowance or savings percentage of the period before.</param>
public sealed record Reported(
    decimal? Increase = null,
    string? EmployeeShare = null,
    IReadOnlyList<decimal?>? Steps = null,
    decimal? TotalPercent = null,
    string? EmployerShare = null,
    decimal? EmployerPoints = null,
    decimal? EmployeePoints = null,
    decimal? Points = null)
{
    public static Reported None { get; } = new();

    /// <summary>Whether a figure of this rule was made from the rule of its kind for the period before.</summary>
    public bool BuildsOnEarlier => Increase is not null || EmployerPoints is not null || EmployeePoints is not null || Points is not null;
}

/// <summary>What one extraction run produced.</summary>
/// <param name="Unreadable">Answers the model gave that could not be read as rules, by kind of rule.</param>
/// <param name="Gaps">
/// What the proposed rules leave for a person to settle, by kind of rule: no rule at all,
/// days from the agreement's start on which no rule applies, or two rules for the same
/// days. None of these is a fault in one rule, so no check on a rule can show it.
/// </param>
public sealed record ExtractionResult(IReadOnlyList<ProposedRule> Rules, IReadOnlyList<RuleProblem> Unreadable, IReadOnlyList<RuleProblem> Gaps)
{
    /// <summary>The rules that passed every check.</summary>
    public IEnumerable<Rule> Sound => Rules.Where(rule => rule.Problems.Count == 0).Select(rule => rule.Rule);
}

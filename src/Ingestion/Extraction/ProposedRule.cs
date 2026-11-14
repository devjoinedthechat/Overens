using Overens.Rules;

namespace Overens.Ingestion.Extraction;

/// <summary>
/// A rule the model read out of an agreement, before a person has approved it.
/// <see cref="AlsoFrom"/> names other clauses the rule draws on, such as the clause that
/// states the daily norm an overtime rule refers to. <see cref="Problems"/> is what checking
/// it without a model found; a rule with any problem cannot be approved as it stands.
/// </summary>
public sealed record ProposedRule(Rule Rule, IReadOnlyList<string> AlsoFrom, IReadOnlyList<RuleProblem> Problems);

/// <summary>What one extraction run produced.</summary>
/// <param name="Unreadable">Answers the model gave that could not be read as rules, by kind of rule.</param>
/// <param name="Gaps">
/// What the proposed rules leave uncovered, by kind of rule: no rule at all, or days from the
/// agreement's start on which no rule applies. A missing rule is not a wrong rule, so no
/// check on a rule can show it.
/// </param>
public sealed record ExtractionResult(IReadOnlyList<ProposedRule> Rules, IReadOnlyList<RuleProblem> Unreadable, IReadOnlyList<RuleProblem> Gaps)
{
    /// <summary>The rules that passed every check.</summary>
    public IEnumerable<Rule> Sound => Rules.Where(rule => rule.Problems.Count == 0).Select(rule => rule.Rule);
}

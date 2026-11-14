using Overens.Documents;
using Overens.Rules;

namespace Overens.Synthetic;

/// <summary>What to generate. The same spec always gives the same agreement.</summary>
/// <param name="Difficulty">
/// 1: one set of rates, in plain sentences. 2: rates that step up each year, in tables, with
/// numbers in words and clauses that refer to each other. 3: also a protocol amendment that
/// overrides the main text, and rates for young workers and apprentices that are not the minimum wage.
/// </param>
public sealed record AgreementSpec(string Id, int Seed, int Difficulty, int StartYear);

/// <summary>A generated agreement and the rules it states, which is the answer an extractor should reach.</summary>
public sealed record SyntheticAgreement(AgreementDocument Document, RuleSet Truth);

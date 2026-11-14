using System.Text.Json.Serialization;
using Overens.Rules;

namespace Overens.Synthetic;

/// <summary>A collective agreement as its structure: sections, numbered clauses, text and tables.</summary>
public sealed record AgreementDocument(
    string Id,
    string Title,
    string Parties,
    IReadOnlyList<Section> Sections,
    IReadOnlyList<Section> Protocols);

/// <summary>A section (<c>§ 9</c>) or a protocol amendment (<c>Protokollat 1</c>).</summary>
public sealed record Section(string Number, string Heading, IReadOnlyList<Clause> Clauses);

/// <summary>A numbered clause, such as <c>§ 9, stk. 2</c>.</summary>
public sealed record Clause(string Id, IReadOnlyList<Block> Blocks);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Paragraph), "paragraph")]
[JsonDerivedType(typeof(TableBlock), "table")]
public abstract record Block;

public sealed record Paragraph(string Text) : Block;

public sealed record TableBlock(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows) : Block;

/// <summary>What to generate. The same spec always gives the same agreement.</summary>
/// <param name="Difficulty">
/// 1: one set of rates, in plain sentences. 2: rates that step up each year, in tables, with
/// numbers in words and clauses that refer to each other. 3: also a protocol amendment that
/// overrides the main text, and rates for young workers and apprentices that are not the minimum wage.
/// </param>
public sealed record AgreementSpec(string Id, int Seed, int Difficulty, int StartYear);

/// <summary>A generated agreement and the rules it states, which is the answer an extractor should reach.</summary>
public sealed record SyntheticAgreement(AgreementDocument Document, RuleSet Truth);

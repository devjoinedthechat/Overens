using System.Text.Json.Serialization;
namespace Overens.Documents;

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

using System.Text.Json.Serialization;
namespace Overens.Documents;

/// <summary>A collective agreement as its structure: sections, numbered clauses, text and tables.</summary>
/// <param name="Id">The agreement's id.</param>
/// <param name="Title">Its title, as on the front page.</param>
/// <param name="Parties">Who agreed it.</param>
/// <param name="Sections">The numbered sections of the main text.</param>
/// <param name="Protocols">What follows the main text: protocol amendments and appendices.</param>
/// <param name="Numbering">How the printed agreement writes its numbers. Null is the usual way: "§ 5. Overarbejde" and "Stk. 2. ".</param>
public sealed record AgreementDocument(
    string Id,
    string Title,
    string Parties,
    IReadOnlyList<Section> Sections,
    IReadOnlyList<Section> Protocols,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Numbering = null)
{
    /// <summary>"§ 5 Overarbejde", with no full stop, and each clause's "Stk. 2" on a line of its own above its text.</summary>
    public const string OwnLine = "own-line";
}

/// <summary>A section (<c>§ 9</c>), a protocol amendment (<c>Protokollat 1</c>) or an appendix (<c>Bilag 1</c>).</summary>
/// <param name="Number">Its number, as it is cited.</param>
/// <param name="Heading">Its heading.</param>
/// <param name="Clauses">Its numbered clauses.</param>
/// <param name="Chapter">The chapter heading printed above this section, when it is the first of a chapter.</param>
public sealed record Section(
    string Number,
    string Heading,
    IReadOnlyList<Clause> Clauses,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Chapter = null);

/// <summary>A numbered clause, such as <c>§ 9, stk. 2</c>.</summary>
public sealed record Clause(string Id, IReadOnlyList<Block> Blocks);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Paragraph), "paragraph")]
[JsonDerivedType(typeof(TableBlock), "table")]
public abstract record Block;

public sealed record Paragraph(string Text) : Block;

public sealed record TableBlock(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows) : Block;

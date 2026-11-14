using System.Text.RegularExpressions;
using Overens.Documents;

namespace Overens.Agent.Questions;

/// <summary>A clause found by a search: where it is and how its text begins.</summary>
public sealed record ClauseHit(string Id, string Heading, string Opening);

/// <summary>Finds the clauses of an agreement that a question is likely about.</summary>
public interface IClauseIndex
{
    IReadOnlyList<ClauseHit> Search(string query, int top = 5);

    /// <summary>A clause's full text, or null when the agreement has no such clause.</summary>
    string? Read(string clauseId);
}

/// <summary>
/// Clause search in memory, by the words a query and a clause share (BM25). An agreement is
/// a few dozen clauses, so nothing heavier is needed to rank them. Danish joins words
/// together, so a word also matches a longer word that starts with it.
/// </summary>
public sealed partial class ClauseIndex : IClauseIndex
{
    private readonly List<(ClauseHit Hit, string Text, List<string> Words)> clauses = [];
    private readonly double averageLength;

    public ClauseIndex(IEnumerable<Section> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        foreach (var section in sections)
        {
            foreach (var clause in section.Clauses)
            {
                var text = ClauseText.Of(clause);
                var opening = text.ReplaceLineEndings(" ");
                clauses.Add((new(clause.Id, section.Heading, opening.Length > 160 ? opening[..160] + "…" : opening), text, Words($"{section.Heading} {text}")));
            }
        }

        averageLength = clauses.Count == 0 ? 1 : clauses.Average(clause => clause.Words.Count);
    }

    public IReadOnlyList<ClauseHit> Search(string query, int top = 5)
    {
        var terms = Words(query ?? "").Distinct().ToList();
        return [.. clauses
            .Select(clause => (clause.Hit, Score: terms.Sum(term => Score(term, clause.Words))))
            .Where(scored => scored.Score > 0)
            .OrderByDescending(scored => scored.Score)
            .Take(top)
            .Select(scored => scored.Hit)];
    }

    public string? Read(string clauseId) => clauses.FirstOrDefault(clause => clause.Hit.Id == clauseId).Text;

    private double Score(string term, List<string> words)
    {
        var inClause = words.Sum(word => Likeness(term, word));
        if (inClause == 0)
        {
            return 0;
        }

        var clausesWith = clauses.Count(clause => clause.Words.Any(word => Likeness(term, word) > 0.5));
        var rarity = Math.Log(1 + ((clauses.Count - clausesWith + 0.5) / (clausesWith + 0.5)));
        return rarity * inClause * 2.2 / (inClause + (1.2 * (0.25 + (0.75 * words.Count / averageLength))));
    }

    /// <summary>
    /// How much a word in a clause counts as the word asked for: fully when they are the same,
    /// and in part when one starts with the other, by how much of the longer one is shared.
    /// "feriegodtgørelsen" is nearly "feriegodtgørelse", and only a little "ferie".
    /// </summary>
    private static double Likeness(string term, string word)
    {
        if (word == term)
        {
            return 1;
        }

        var (shorter, longer) = term.Length < word.Length ? (term, word) : (word, term);
        return shorter.Length >= 5 && longer.StartsWith(shorter, StringComparison.Ordinal) ? (double)shorter.Length / longer.Length : 0;
    }

    private static List<string> Words(string text) =>
        [.. Word().Matches(text.ToLowerInvariant()).Select(match => match.Value).Where(word => word.Length > 1)];

    [GeneratedRegex(@"\p{L}+|\d+(?:[.,]\d+)?")]
    private static partial Regex Word();
}

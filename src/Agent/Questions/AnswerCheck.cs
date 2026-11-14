using System.Globalization;
using System.Text.RegularExpressions;

namespace Overens.Agent.Questions;

/// <summary>A clause an answer rests on, and the words in it that carry the answer.</summary>
public sealed record Citation(string ClauseId, string Quote);

/// <summary>
/// Decides, with no model, whether an answer may be shown:
///
/// - it cites at least one clause;
/// - every cited clause exists and every quote stands in its clause word for word;
/// - every number in the answer is printed in a cited clause, was in the question, or was
///   put into or came out of the rule lookup or the calculator in this conversation.
///
/// It cannot tell whether the answer is right. It can tell that the answer rests on the
/// agreement's own words and on numbers the model did not make up.
/// </summary>
public static partial class AnswerCheck
{
    /// <summary>Why an answer may not be shown, or null when it may.</summary>
    public static string? Refusal(string question, string text, IReadOnlyList<Citation> citations, IClauseIndex index, IReadOnlyList<string> computed)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(citations);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(computed);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "The answer is empty.";
        }

        if (text.Length > 1500)
        {
            return "The answer is longer than 1500 characters.";
        }

        if (citations.Count == 0)
        {
            return "The answer cites no clause.";
        }

        var allowed = new HashSet<decimal>(FindingExplainer.Numbers(question));
        foreach (var citation in citations)
        {
            if (index.Read(citation.ClauseId) is not { } clause)
            {
                return $"The agreement has no clause \"{citation.ClauseId}\".";
            }

            var quote = Flatten(citation.Quote);
            if (quote.Length < 8 || !Flatten(clause).Contains(quote, StringComparison.Ordinal))
            {
                return $"The quote from {citation.ClauseId} does not stand in that clause word for word.";
            }

            allowed.UnionWith(FindingExplainer.Numbers(clause));
            allowed.UnionWith(FindingExplainer.Numbers(citation.ClauseId));
        }

        // Tool results are JSON, where a number is written 1234.5 and never 1.234,50.
        foreach (Match number in JsonNumber().Matches(string.Join(' ', computed)))
        {
            allowed.Add(decimal.Parse(number.Value, CultureInfo.InvariantCulture));
        }

        var foreign = FindingExplainer.Numbers(text).Where(number => !allowed.Contains(number)).Distinct().ToList();
        return foreign.Count == 0
            ? null
            : $"These numbers are not in a cited clause, in a tool result or in the question: {string.Join(", ", foreign.Select(number => number.ToString(CultureInfo.InvariantCulture)))}.";
    }

    private static string Flatten(string text) => WhiteSpace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex JsonNumber();
}

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
///   put into or came out of the rule lookup or the calculator in this conversation;
/// - a number spelled out as a word stands, as that word, in a cited clause or the question;
/// - an amount in kroner or a percentage comes from the approved rules: from the lookup or
///   the calculator, or from a clause an approved rule rests on. A clause nobody approved a
///   rule from can say anything, so its money is not repeated.
///
/// It cannot tell whether the answer is right. It can tell that the answer rests on the
/// agreement's own words, on numbers the model did not make up, and for money on what a
/// person approved.
/// </summary>
public static partial class AnswerCheck
{
    /// <summary>Why an answer may not be shown, or null when it may.</summary>
    /// <param name="question">What was asked.</param>
    /// <param name="text">The answer.</param>
    /// <param name="citations">The clauses the answer says it rests on.</param>
    /// <param name="index">The agreement.</param>
    /// <param name="computed">What the rule lookup and the calculator were asked and answered.</param>
    /// <param name="ruleClauses">The clauses the approved rules rest on.</param>
    public static string? Refusal(string question, string text, IReadOnlyList<Citation> citations, IClauseIndex index, IReadOnlyList<string> computed, IReadOnlySet<string> ruleClauses)
    {
        ArgumentNullException.ThrowIfNull(ruleClauses);
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

        // Numbers anyone may repeat, and the narrower set that money may come from.
        var allowed = new HashSet<decimal>(FindingExplainer.Numbers(question));
        var approved = new HashSet<decimal>(allowed);
        var words = new HashSet<string>(FindingExplainer.NumberWords(question));
        foreach (var citation in citations)
        {
            var wordings = index.Wordings(citation.ClauseId);
            if (wordings.Count == 0)
            {
                return $"The agreement has no clause \"{citation.ClauseId}\".";
            }

            var quote = Flatten(citation.Quote);
            if (quote.Length < 8 || wordings.FirstOrDefault(wording => Flatten(wording).Contains(quote, StringComparison.Ordinal)) is not { } clause)
            {
                return $"The quote from {citation.ClauseId} does not stand in that clause word for word.";
            }

            var printed = FindingExplainer.Numbers(clause).Concat(FindingExplainer.Numbers(citation.ClauseId)).ToList();
            allowed.UnionWith(printed);
            words.UnionWith(FindingExplainer.NumberWords(clause));
            if (ruleClauses.Contains(citation.ClauseId))
            {
                approved.UnionWith(printed);
            }
        }

        // Tool results are JSON, where a number is written 1234.5 and never 1.234,50.
        foreach (Match number in JsonNumber().Matches(string.Join(' ', computed)))
        {
            var value = decimal.Parse(number.Value, CultureInfo.InvariantCulture);
            allowed.Add(value);
            approved.Add(value);
        }

        var foreign = FindingExplainer.Numbers(text).Where(number => !allowed.Contains(number)).Distinct().ToList();
        if (foreign.Count > 0)
        {
            return $"These numbers are not in a cited clause, in a tool result or in the question: {string.Join(", ", foreign.Select(number => number.ToString(CultureInfo.InvariantCulture)))}.";
        }

        // A number spelled out is held to the same rule: it must stand, as a word, in a cited clause or in the question.
        var spelled = FindingExplainer.NumberWords(text).Where(word => !words.Contains(word)).ToList();
        if (spelled.Count > 0)
        {
            return $"These numbers are written as words and are not in a cited clause: {string.Join(", ", spelled)}. Write a number in digits, as the agreement or the tool gave it.";
        }

        var unapproved = Money().Matches(text).SelectMany(match => FindingExplainer.Numbers(match.Groups[1].Value)).Where(number => !approved.Contains(number)).Distinct().ToList();
        return unapproved.Count == 0
            ? null
            : $"These amounts or percentages are not from the approved rules: {string.Join(", ", unapproved.Select(number => number.ToString(CultureInfo.InvariantCulture)))}. Look them up with rules_in_force or calculate_pay, or leave them out.";
    }

    /// <summary>A number followed by kroner or per cent.</summary>
    [GeneratedRegex(@"(\d[\d.,]*)\s*(?:kr\b|kroner|pct|procent|%)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Money();

    private static string Flatten(string text) => WhiteSpace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex JsonNumber();
}

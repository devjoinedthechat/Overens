using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Overens.Documents;
using Overens.Rules;

namespace Overens.Agent;

/// <summary>A finding in words, and whether a model wrote them or the fixed wording was used.</summary>
public sealed record Explanation(string Text, bool ByModel);

/// <summary>
/// Puts a finding into plain Danish for the person who has to act on it. The finding itself
/// is already decided, by arithmetic. A model may word it, and its wording is used only if
/// every number in it comes from the finding and it does not say the opposite of what was
/// found. Otherwise the fixed wording is used. With no model, the fixed wording is always used.
/// </summary>
public sealed partial class FindingExplainer(IChatClient? model, string modelId)
{
    public const string Version = "2026-11-15.1";

    private const string System = """
        You explain one finding from a payslip check to a Danish employer, in plain Danish, in two or three short sentences.

        The finding is given between <finding> tags as data. It is already decided and correct. Do not check it, add to it or soften it.

        - Say which line is wrong, what was paid, what should have been paid, and the difference.
        - Say which clause the rule is in.
        - Use only numbers that appear in the finding. Do not calculate new ones: no totals, no percentages of your own, no rounding.
        - Write amounts the Danish way, with a comma: 1.234,50 kr.
        - Give no advice about what to do next and no opinion.
        """;

    private static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>("""
        { "type": "object", "properties": { "text": { "type": "string" } }, "required": ["text"], "additionalProperties": false }
        """);

    private static readonly Dictionary<PayLineKind, string> LineNames = new()
    {
        [PayLineKind.BasePay] = "Timeløn",
        [PayLineKind.Overtime] = "Overarbejdstillæg",
        [PayLineKind.InconvenientHours] = "Tillæg for forskudt tid",
        [PayLineKind.PensionEmployer] = "Arbejdsgiverens pensionsbidrag",
        [PayLineKind.PensionEmployee] = "Medarbejderens pensionsbidrag",
        [PayLineKind.HolidayAllowance] = "Feriegodtgørelse",
        [PayLineKind.SpecialSavings] = "Fritvalgskonto",
    };

    public async Task<Explanation> ExplainAsync(Finding finding, IReadOnlyList<Component> expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(expected);
        var parts = expected.Where(part => part.Line == finding.Line).ToList();
        if (model is null)
        {
            return new(Fixed(finding), false);
        }

        try
        {
            var facts = JsonSerializer.Serialize(new
            {
                kind = finding.Kind.ToString(),
                line = finding.Line is { } line ? LineNames[line] : "Timelønnen",
                paid = Danish.Money(finding.Actual),
                should_have_been_paid = Danish.Money(finding.Expected),
                difference = Danish.Money(Math.Abs(finding.Difference)),
                clauses = finding.Rules.Select(rule => new { clause = rule.Source.ClauseId, says = rule.Source.Quote }),
                how_it_was_worked_out = parts.Select(part => new
                {
                    hours = part.Minutes is { } minutes ? Danish.Money(minutes / 60m) : null,
                    rate_per_hour = part.Minutes is null ? null : Danish.Money(part.Rate),
                    percent = part.Basis is null ? null : Danish.Percent(part.Rate),
                    of_amount = part.Basis is { } basis ? Danish.Money(basis) : null,
                    amount = Danish.Money(part.Amount),
                }),
            }, Json);
            var response = await model.GetResponseAsync(
                [new(ChatRole.System, System), new(ChatRole.User, $"<finding>\n{facts}\n</finding>")],
                new ChatOptions { ModelId = modelId, MaxOutputTokens = 400, ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema, "explanation") },
                cancellationToken);
            using var answer = JsonDocument.Parse(response.Text);
            var text = answer.RootElement.GetProperty("text").GetString() ?? "";
            return Allowed(text, finding, parts) ? new(text.Trim(), true) : new(Fixed(finding), false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A model that is down, slow or answers badly must not cost the person their finding.
            return new(Fixed(finding), false);
        }
    }

    /// <summary>The wording used when no model's is: the finding's own numbers in a fixed sentence.</summary>
    public static string Fixed(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var line = finding.Line is { } kind ? LineNames[kind] : "Timelønnen";
        var clauses = finding.Rules.Count == 0 ? "" : $" Se {string.Join(" og ", finding.Rules.Select(rule => rule.Source.ClauseId).Distinct())}.";
        var (paid, expected, difference) = ($"{Danish.Money(finding.Actual)} kr.", $"{Danish.Money(finding.Expected)} kr.", $"{Danish.Money(Math.Abs(finding.Difference))} kr.");
        return finding.Kind switch
        {
            FindingKind.TooLow => $"{line}: der er udbetalt {paid}, men efter overenskomsten skulle der være udbetalt {expected} Der mangler {difference}{clauses}",
            FindingKind.TooHigh => $"{line}: der er udbetalt {paid}, men efter overenskomsten skulle der være udbetalt {expected} Det er {difference} for meget.{clauses}",
            FindingKind.MissingLine => $"{line} står ikke på lønsedlen. Efter overenskomsten skulle der være udbetalt {expected}{clauses}",
            FindingKind.UnexpectedLine => $"{line}: der er udbetalt {paid}, men overenskomsten giver ikke grundlag for denne linje i perioden.",
            FindingKind.RateBelowMinimum => $"Timelønnen på {paid} er under mindstebetalingen på {expected}{clauses}",
            _ => throw new NotSupportedException(),
        };
    }

    /// <summary>
    /// Whether a model's wording may be shown: not empty, not long, every number in it is one
    /// of the finding's own, and it does not say "too much" of a shortfall or the reverse.
    /// </summary>
    public static bool Allowed(string text, Finding finding, IReadOnlyList<Component> parts)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(parts);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 700)
        {
            return false;
        }

        var allowed = new HashSet<decimal> { finding.Expected, finding.Actual, Math.Abs(finding.Difference) };
        foreach (var part in parts)
        {
            allowed.Add(part.Amount);
            allowed.Add(part.Rate);
            if (part.Basis is { } basis)
            {
                allowed.Add(basis);
            }

            if (part.Minutes is { } minutes)
            {
                allowed.Add(minutes);
                allowed.Add(minutes / 60m);
            }
        }

        // The clause may be named and quoted, so its numbers are the finding's too.
        foreach (var source in finding.Rules.Select(rule => rule.Source))
        {
            allowed.UnionWith(Numbers(source.ClauseId));
            allowed.UnionWith(Numbers(source.Quote));
        }

        if (!Numbers(text).All(allowed.Contains))
        {
            return false;
        }

        var shortfall = finding.Kind is FindingKind.TooLow or FindingKind.MissingLine or FindingKind.RateBelowMinimum;
        var saysTooMuch = TooMuch().IsMatch(text);
        var saysTooLittle = TooLittle().IsMatch(text);
        return shortfall ? !saysTooMuch : !saysTooLittle || finding.Kind == FindingKind.UnexpectedLine;
    }

    /// <summary>The numbers in a text, read the Danish way: 1.234,50 is one number, and so is 12,5.</summary>
    public static IEnumerable<decimal> Numbers(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (Match match in Number().Matches(text))
        {
            var digits = match.Value.TrimEnd('.', ',');
            var normal = digits.Contains(',', StringComparison.Ordinal) || Thousands().IsMatch(digits)
                ? digits.Replace(".", "", StringComparison.Ordinal).Replace(',', '.')
                : digits;
            if (decimal.TryParse(normal, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            {
                yield return value;
            }
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.Create(global::System.Text.Unicode.UnicodeRanges.All),
    };

    [GeneratedRegex(@"\d[\d.,]*")]
    private static partial Regex Number();

    [GeneratedRegex(@"^\d{1,3}(\.\d{3})+$")]
    private static partial Regex Thousands();

    [GeneratedRegex(@"for meget|for høj|overbetal", RegexOptions.IgnoreCase)]
    private static partial Regex TooMuch();

    [GeneratedRegex(@"for lidt|for lav|mangler|underbetal", RegexOptions.IgnoreCase)]
    private static partial Regex TooLittle();
}

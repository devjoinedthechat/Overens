using System.Globalization;
using System.Text.RegularExpressions;

namespace Overens.Rules;

/// <summary>
/// What a rule says, apart from its name and the clause it is quoted from. Two rules that
/// pay the same are the same rule, however they were written down: <c>6.0</c> and <c>6</c>
/// are one number, and a night window of 22–06 is the same as 22–24 and 00–06, since time
/// windows are compared by what they pay at each half hour of the week.
/// </summary>
public static partial class RuleMeaning
{
    /// <summary>What the rule says, and the days it says it on.</summary>
    public static string Of(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return $"{rule.InForce} {Pays(rule)}";
    }

    /// <summary>What the rule says, whatever days it applies on.</summary>
    public static string Pays(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule is InconvenientHoursRule hours)
        {
            var monday = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            var week = Enumerable.Range(0, 7 * 48).Select(slot => hours.AmountAt(monday.AddMinutes(slot * 30)).ToString("0.####", CultureInfo.InvariantCulture));
            return $"inconvenient_hours {string.Join(' ', week)}";
        }

        // 6.0 and 6 are the same number: trailing zeros are dropped before comparing.
        return TrailingZeros().Replace(RuleJson.Write(rule with { Id = "", InForce = default, Source = new("", "") }), "$1$2");
    }

    [GeneratedRegex(@"(\d)\.0+(?=\D)|(\.\d*?[1-9])0+(?=\D)")]
    private static partial Regex TrailingZeros();
}

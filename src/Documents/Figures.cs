using System.Globalization;
using System.Text.RegularExpressions;

namespace Overens.Documents;

/// <summary>What a figure in a Danish text is.</summary>
public enum FigureKind
{
    /// <summary>A number in digits: "142,50", "12,5", "1.234,50", "7".</summary>
    Number,

    /// <summary>A small number in words: "to", "atten", "et".</summary>
    Word,

    /// <summary>An ordinal: "første", "anden", or in digits "2." before a word, as in "2. år".</summary>
    Ordinal,

    /// <summary>A day: "1. marts 2026", "den første marts 2026", "01.03.2026", "1.3.2026". Without a year it is still a date, and names no day.</summary>
    Date,

    /// <summary>A time of day: "18.00", "kl. 06:00". "24.00" is midnight.</summary>
    Clock,

    /// <summary>A number that names something: "§ 3", "stk. 2", "Bilag 1", "Løntrin 2".</summary>
    Reference,
}

/// <summary>One figure, where it stands in the text, and its value.</summary>
/// <param name="Value">The number, for numbers, number words, ordinals and references.</param>
/// <param name="Day">The day, for a date that prints its year.</param>
/// <param name="Time">The time, for a clock time.</param>
/// <param name="Decimals">Whether a number was printed with decimals: "10,0" is ten, and it is not a count of ten months.</param>
public sealed record Figure(FigureKind Kind, int Start, int End, decimal Value = 0, DateOnly? Day = null, TimeOnly? Time = null, bool Decimals = false);

/// <summary>
/// Reads the figures out of Danish text, once, so that a check asks whether a value is there
/// and not whether one of the strings it could be printed as is. "§ 3" is not a 3, "1. marts
/// 2027" is a date and not a 1, "2. år" is the second year, and "12,50" is 12,5.
/// </summary>
public static partial class Figures
{
    private static readonly string[] Months =
        ["januar", "februar", "marts", "april", "maj", "juni", "juli", "august", "september", "oktober", "november", "december"];

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nul"] = 0,
        ["en"] = 1,
        ["et"] = 1,
        ["ét"] = 1,
        ["én"] = 1,
        ["to"] = 2,
        ["tre"] = 3,
        ["fire"] = 4,
        ["fem"] = 5,
        ["seks"] = 6,
        ["syv"] = 7,
        ["otte"] = 8,
        ["ni"] = 9,
        ["ti"] = 10,
        ["elleve"] = 11,
        ["tolv"] = 12,
        ["tretten"] = 13,
        ["fjorten"] = 14,
        ["femten"] = 15,
        ["seksten"] = 16,
        ["sytten"] = 17,
        ["atten"] = 18,
        ["nitten"] = 19,
        ["tyve"] = 20,
    };

    private static readonly Dictionary<string, int> OrdinalWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["første"] = 1,
        ["anden"] = 2,
        ["andet"] = 2,
        ["tredje"] = 3,
        ["fjerde"] = 4,
        ["femte"] = 5,
        ["sjette"] = 6,
        ["syvende"] = 7,
        ["ottende"] = 8,
        ["niende"] = 9,
        ["tiende"] = 10,
    };

    /// <summary>Every figure in the text, in order.</summary>
    public static IReadOnlyList<Figure> In(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var figures = new List<Figure>();
        foreach (Match match in Pattern().Matches(text))
        {
            var (start, end) = (match.Index, match.Index + match.Length);
            if (match.Groups["ref"].Success)
            {
                figures.Add(new(FigureKind.Reference, start, end, Whole(match.Groups["refn"].Value)));
            }
            else if (match.Groups["wdate"].Success)
            {
                var day = match.Groups["wd"].Success ? int.Parse(match.Groups["wd"].Value, CultureInfo.InvariantCulture) : 1;
                var month = Array.IndexOf(Months, match.Groups["wm"].Value.ToLowerInvariant()) + 1;
                figures.Add(new(FigureKind.Date, start, end, Day: match.Groups["wy"].Success ? Day(int.Parse(match.Groups["wy"].Value, CultureInfo.InvariantCulture), month, day) : null));
            }
            else if (match.Groups["ndate"].Success)
            {
                figures.Add(new(FigureKind.Date, start, end, Day: Day(
                    int.Parse(match.Groups["ny"].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["nm"].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["nd"].Value, CultureInfo.InvariantCulture))));
            }
            else if (match.Groups["clock"].Success && int.Parse(match.Groups["ch"].Value, CultureInfo.InvariantCulture) is var hour and <= 24
                && int.Parse(match.Groups["cm"].Value, CultureInfo.InvariantCulture) is var minute and < 60 && (hour < 24 || minute == 0))
            {
                figures.Add(new(FigureKind.Clock, start, end, Time: new(hour % 24, minute)));
            }
            else if (match.Groups["clock"].Success)
            {
                // Two digits after a full stop that are no time of day: read as a number with a thousands mark would be, which it is not either.
                continue;
            }
            else if (match.Groups["num"].Success)
            {
                var value = decimal.Parse(match.Groups["int"].Value.Replace(".", "", StringComparison.Ordinal) + (match.Groups["frac"].Success ? "." + match.Groups["frac"].Value : ""), CultureInfo.InvariantCulture);
                var ordinal = match.Groups["ord"].Success && !match.Groups["frac"].Success;
                figures.Add(new(ordinal ? FigureKind.Ordinal : FigureKind.Number, start, end, value, Decimals: match.Groups["frac"].Success));
            }
            else if (match.Groups["word"].Success)
            {
                figures.Add(new(FigureKind.Word, start, end, NumberWords[match.Groups["word"].Value]));
            }
            else if (match.Groups["ordw"].Success)
            {
                figures.Add(new(FigureKind.Ordinal, start, end, OrdinalWords[match.Groups["ordw"].Value]));
            }
        }

        return figures;
    }

    private static decimal Whole(string digits) => decimal.Parse(digits, CultureInfo.InvariantCulture);

    private static DateOnly? Day(int year, int month, int day) =>
        month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new(year, month, day) : null;

    [GeneratedRegex(
        """
        (?<ref>(?:§|\bstk\.|\bbilag|\bkapitel|\bløntrin|\btrin|\blønklasse|\bgruppe|\bniveau|\bprotokollat|\bpkt\.)\s*(?<refn>\d+))
        |(?<wdate>(?:(?<![\d.,])(?<wd>\d{1,2})\.|\b(?<wfirst>første))\s*(?<wm>januar|februar|marts|april|maj|juni|juli|august|september|oktober|november|december)\b(?:\s+(?<wy>\d{4})\b)?)
        |(?<ndate>(?<![\d.,])(?<nd>\d{1,2})\.(?<nm>\d{1,2})\.(?<ny>\d{4})(?!\d))
        |(?<clock>(?<![\d.,])(?<ch>\d{1,2})[.:](?<cm>\d{2})(?![\d]|,\d))
        |(?<num>(?<![\d.,])(?<int>\d{1,3}(?:\.\d{3})+(?!\d)|\d+)(?:,(?<frac>\d+))?(?!\d)(?<ord>\.(?=\s+(?-i:\p{Ll})))?)
        |(?<word>\b(?:nul|en|et|ét|én|to|tre|fire|fem|seks|syv|otte|ni|ti|elleve|tolv|tretten|fjorten|femten|seksten|sytten|atten|nitten|tyve)\b)
        |(?<ordw>\b(?:første|anden|andet|tredje|fjerde|femte|sjette|syvende|ottende|niende|tiende)\b)
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Pattern();
}

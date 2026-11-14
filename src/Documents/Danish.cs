using System.Globalization;

namespace Overens.Documents;

/// <summary>Numbers, dates and times the way a Danish agreement writes them.</summary>
public static class Danish
{
    private static readonly string[] Months =
    [
        "januar", "februar", "marts", "april", "maj", "juni",
        "juli", "august", "september", "oktober", "november", "december",
    ];

    private static readonly string[] Words =
        ["nul", "en", "to", "tre", "fire", "fem", "seks", "syv", "otte", "ni", "ti", "elleve", "tolv"];

    /// <summary>142,50</summary>
    public static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>An amount as a payslip prints it, with a dot between thousands: 1.234,50</summary>
    public static string Money(decimal value) =>
        value.ToString("#,##0.00", CultureInfo.InvariantCulture).Replace(',', ' ').Replace('.', ',').Replace(' ', '.');

    /// <summary>142,50 kr.</summary>
    public static string Kroner(decimal value) => $"{Amount(value)} kr.";

    /// <summary>12,5 pct. and 8,0 pct.</summary>
    public static string Percent(decimal value) => $"{value.ToString("0.0#", CultureInfo.InvariantCulture).Replace('.', ',')} pct.";

    /// <summary>1. marts 2026</summary>
    public static string Date(DateOnly day) => $"{day.Day}. {Months[day.Month - 1]} {day.Year}";

    /// <summary>kl. 18.00</summary>
    public static string Clock(TimeOnly time) => time == TimeOnly.MinValue ? "kl. 24.00" : $"kl. {time:HH}.{time:mm}";

    /// <summary>A small whole number as a word: "to".</summary>
    public static string Word(int number) => number < Words.Length ? Words[number] : number.ToString(CultureInfo.InvariantCulture);
}

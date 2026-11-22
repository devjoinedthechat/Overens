using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Overens.Gateway;
using Overens.Ingestion.Reading;
using Overens.Rules;

namespace Overens.Ingestion.Extraction;

/// <summary>
/// Asks a model for the rules an agreement states, then checks every answer without one.
/// The model proposes. Nothing here publishes anything.
/// </summary>
public sealed class RuleExtractor(IChatClient model, string modelId)
{
    public async Task<ExtractionResult> ExtractAsync(ReadAgreement agreement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agreement);
        var document = ExtractionPrompt.Document(agreement);

        async Task<(RuleKind Kind, string Text)> Ask(RuleKind kind)
        {
            var response = await model.GetResponseAsync(
                [
                    new(ChatRole.System, ExtractionPrompt.System),
                    // The agreement is not marked for the provider to keep between calls. A kept prompt is read
                    // again only when everything before it is the same, the answer's schema included, and each
                    // kind has its own schema: marked, every call paid to store the agreement and none read it.
                    new(ChatRole.User, $"{document}\n\n<task kind=\"{kind.Name}\">\n{kind.Task.Trim()}\n</task>"),
                ],
                new ChatOptions
                {
                    ModelId = modelId,
                    MaxOutputTokens = 4000,
                    ResponseFormat = ChatResponseFormat.ForJsonSchema(kind.Schema, kind.Name),
                },
                cancellationToken);
            return (kind, response.RequireComplete().Text);
        }

        // The kinds do not depend on each other, so they are asked for at the same time.
        var answers = await Task.WhenAll(ExtractionPrompt.Kinds.Select(Ask));

        var rules = new List<(Rule Rule, IReadOnlyList<string> AlsoFrom, Reported Reported)>();
        var unreadable = new List<RuleProblem>();
        foreach (var (kind, text) in answers)
        {
            try
            {
                using var answer = JsonDocument.Parse(text);
                // In date order, so that a period built on the one before finds it.
                foreach (var item in answer.RootElement.GetProperty("rules").EnumerateArray().OrderBy(item => item.GetProperty("from").GetString(), StringComparer.Ordinal))
                {
                    // A pension with no employee's share, given in any way, is not a rule. Left out, it shows as a gap.
                    if (kind.Name == "pension" && Optional(item, "employee_percent") is null && OptionalText(item, "employee_share") is null
                        && Optional(item, "employee_points") is null && Optional(item, "total_percent") is null)
                    {
                        continue;
                    }

                    var (rule, reported) = Read(kind.Name, item, rules.Select(other => other.Rule).Where(other => other.GetType() == KindType(kind.Name)).LastOrDefault());

                    // Ids are given here, never by the model: the kind and the year it starts.
                    var id = $"{kind.Name}-{rule.InForce.From.Year}";
                    while (rules.Any(other => other.Rule.Id == id))
                    {
                        id += "b";
                    }

                    rules.Add((rule with { Id = id }, [.. item.GetProperty("also_from").EnumerateArray().Select(clause => clause.GetString()!)], reported));
                }
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or InvalidOperationException or ArgumentException or OverflowException)
            {
                unreadable.Add(new(kind.Name, "unreadable_answer", error.Message));
            }
        }

        return new(Grounding.Check(agreement, rules), unreadable, Grounding.Gaps([.. rules.Select(rule => rule.Rule)]));
    }

    private static Type KindType(string kind) => kind switch
    {
        "minimum_wage" => typeof(MinimumWageRule),
        "overtime" => typeof(OvertimeRule),
        "inconvenient_hours" => typeof(InconvenientHoursRule),
        "pension" => typeof(PensionRule),
        "holiday_allowance" => typeof(HolidayAllowanceRule),
        "special_savings" => typeof(SpecialSavingsRule),
        _ => throw new InvalidOperationException($"Unknown kind of rule {kind}."),
    };

    /// <summary>A number the schema allows to be null. A field left out is read as null.</summary>
    private static decimal? Optional(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;

    private static string? OptionalText(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// One rule from one item of the answer. Where the model reported a figure as the
    /// agreement prints it, an increase, a step, a share or a change in points, the code works
    /// out the final figure here, from <paramref name="before"/> where it builds on the period before.
    /// </summary>
    private static (Rule Rule, Reported Reported) Read(string kind, JsonElement item, Rule? before)
    {
        var period = new Period(Day(item.GetProperty("from").GetString()!), item.GetProperty("until").GetString() is { } until ? Day(until) : null);
        var source = new Source(item.GetProperty("clause_id").GetString()!, item.GetProperty("quote").GetString()!);
        decimal Number(string name) => item.GetProperty(name).GetDecimal();
        T Before<T>(string what)
            where T : Rule => before as T ?? throw new FormatException($"{what} is given as a change on the period before, and there is no period before.");

        switch (kind)
        {
            case "minimum_wage":
                {
                    var increase = Optional(item, "increase");
                    var bands = new List<SeniorityBand>();
                    var steps = new List<decimal?>();
                    foreach (var band in item.GetProperty("bands").EnumerateArray())
                    {
                        var step = Optional(band, "added_to_band_before");
                        var rate = step is { } added
                            ? (bands.Count > 0 ? bands[^1].HourlyRate : throw new FormatException("The first seniority band is given as an amount added to a band before it.")) + added
                            : Optional(band, "hourly_rate") ?? throw new FormatException("A seniority band has neither a rate nor an amount added to the band before.");
                        bands.Add(new(band.GetProperty("from_months").GetInt32(), rate));
                        steps.Add(step);
                    }

                    if (increase is { } raise)
                    {
                        // The model reports the increase as printed. The sum is made here.
                        var earlier = Before<MinimumWageRule>("A minimum wage");
                        return (new MinimumWageRule("", period, source, [.. earlier.Bands.Select(band => band with { HourlyRate = band.HourlyRate + raise })]), new(Increase: raise));
                    }

                    return (new MinimumWageRule("", period, source, bands), steps.Any(step => step is not null) ? new(Steps: steps) : Reported.None);
                }

            case "overtime":
                return (new OvertimeRule("", period, source,
                    item.GetProperty("daily_norm_minutes").GetInt32() / 60m,
                    [.. item.GetProperty("tiers").EnumerateArray().Select(tier => new OvertimeTier(
                        tier.GetProperty("minutes").ValueKind == JsonValueKind.Null ? null : tier.GetProperty("minutes").GetInt32() / 60m,
                        tier.GetProperty("percent").GetDecimal()))],
                    Number("sunday_percent")), Reported.None);
            case "inconvenient_hours":
                return (new InconvenientHoursRule("", period, source,
                    [.. item.GetProperty("windows").EnumerateArray().Select(window => new TimeWindow(
                        [.. window.GetProperty("days").EnumerateArray().Select(day => Enum.Parse<DayOfWeek>(day.GetString()!))],
                        Time(window.GetProperty("from").GetString()!),
                        Time(window.GetProperty("to").GetString()!),
                        window.GetProperty("amount_per_hour").GetDecimal()))]), Reported.None);
            case "pension":
                {
                    var reported = new Reported(
                        EmployeeShare: OptionalText(item, "employee_share"),
                        TotalPercent: Optional(item, "total_percent"),
                        EmployerShare: OptionalText(item, "employer_share"),
                        EmployerPoints: Optional(item, "employer_points"),
                        EmployeePoints: Optional(item, "employee_points"));
                    decimal employer, employee;
                    if (reported is { TotalPercent: { } total, EmployerShare: { } employerPart })
                    {
                        // "13,2 pct. i alt, hvoraf arbejdsgiveren betaler to tredjedele": the employer's part, and the employee the rest.
                        employer = Shares.Of(employerPart, total);
                        employee = total - employer;
                    }
                    else if (reported.TotalPercent is not null && (Optional(item, "employer_percent") is null || (Optional(item, "employee_percent") is null && reported.EmployeeShare is null && reported.EmployeePoints is null)))
                    {
                        throw new FormatException("A pension total is given without the employer's part of it, and without both shares.");
                    }
                    else
                    {
                        employer = reported.EmployerPoints is { } employerPoints ? Before<PensionRule>("The employer's pension share").EmployerPercent + employerPoints
                            : Optional(item, "employer_percent") ?? throw new FormatException("A pension rule has no employer's share.");
                        employee = reported.EmployeeShare is { } part ? Shares.Of(part, employer)
                            : reported.EmployeePoints is { } employeePoints ? Before<PensionRule>("The employee's pension share").EmployeePercent + employeePoints
                            : Number("employee_percent");
                    }

                    // A total printed beside both shares is no share of its own. It is kept, so that the shares can be held to it.
                    return (new PensionRule("", period, source, employer, employee, item.GetProperty("qualifying_months").GetInt32()), reported == Reported.None ? Reported.None : reported);
                }

            case "holiday_allowance":
            case "special_savings":
                {
                    var points = Optional(item, "points_added");
                    decimal Earlier() => Before<Rule>("A percentage") switch
                    {
                        HolidayAllowanceRule holiday => holiday.Percent,
                        SpecialSavingsRule savings => savings.Percent,
                        _ => throw new FormatException("A percentage is given as a change on a rule of another kind."),
                    };
                    var percent = points is { } added ? Earlier() + added : Optional(item, "percent") ?? throw new FormatException("The rule has no percentage.");
                    Rule rule = kind == "holiday_allowance" ? new HolidayAllowanceRule("", period, source, percent) : new SpecialSavingsRule("", period, source, percent);
                    return (rule, points is null ? Reported.None : new(Points: points));
                }

            default:
                throw new InvalidOperationException($"Unknown kind of rule {kind}.");
        }

        static DateOnly Day(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        static TimeOnly Time(string text) => TimeOnly.ParseExact(text, "HH:mm", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// A part of another figure, as a model may report it when the agreement gives a share in
/// words and prints no percentage.
/// </summary>
public static class Shares
{
    private static readonly Dictionary<string, (int Parts, int Of, string[] Words)> Known = new(StringComparer.Ordinal)
    {
        ["half"] = (1, 2, ["halvdelen", "det halve", "1/2"]),
        ["third"] = (1, 3, ["en tredjedel", "1/3"]),
        ["two_thirds"] = (2, 3, ["to tredjedele", "2/3"]),
        ["quarter"] = (1, 4, ["en fjerdedel", "1/4"]),
        ["three_quarters"] = (3, 4, ["tre fjerdedele", "3/4"]),
    };

    /// <summary>That share of a figure. Multiplied first and divided once, so half of 8,0 is 4,0 and nothing is lost on the way.</summary>
    public static decimal Of(string share, decimal whole) =>
        Known.TryGetValue(share, out var known) ? whole * known.Parts / known.Of : throw new FormatException($"Unknown share \"{share}\".");

    /// <summary>The ways an agreement writes the share. One of them must be printed where the rule cites.</summary>
    public static IReadOnlyList<string> Words(string share) => Known[share].Words;
}

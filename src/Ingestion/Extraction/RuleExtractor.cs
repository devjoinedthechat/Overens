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

        var rules = new List<(Rule Rule, IReadOnlyList<string> AlsoFrom, decimal? Increase)>();
        var unreadable = new List<RuleProblem>();
        foreach (var (kind, text) in answers)
        {
            try
            {
                using var answer = JsonDocument.Parse(text);
                // In date order, so that a period given as an increase finds the period it builds on.
                foreach (var item in answer.RootElement.GetProperty("rules").EnumerateArray().OrderBy(item => item.GetProperty("from").GetString(), StringComparer.Ordinal))
                {
                    var rule = Read(kind.Name, item);
                    decimal? increase = null;
                    if (rule is MinimumWageRule wage && item.GetProperty("increase").ValueKind == JsonValueKind.Number)
                    {
                        // The model reports the increase as printed. The sum is made here.
                        increase = item.GetProperty("increase").GetDecimal();
                        var before = rules.Select(other => other.Rule).OfType<MinimumWageRule>().LastOrDefault()
                            ?? throw new FormatException("A minimum wage is given as an increase, with no earlier rates to increase.");
                        rule = wage with { Bands = [.. before.Bands.Select(band => band with { HourlyRate = band.HourlyRate + increase.Value })] };
                    }

                    // Ids are given here, never by the model: the kind and the year it starts.
                    var id = $"{kind.Name}-{rule.InForce.From.Year}";
                    while (rules.Any(other => other.Rule.Id == id))
                    {
                        id += "b";
                    }

                    rules.Add((rule with { Id = id }, [.. item.GetProperty("also_from").EnumerateArray().Select(clause => clause.GetString()!)], increase));
                }
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or InvalidOperationException or ArgumentException or OverflowException)
            {
                unreadable.Add(new(kind.Name, "unreadable_answer", error.Message));
            }
        }

        return new(Grounding.Check(agreement, rules), unreadable, Grounding.Gaps([.. rules.Select(rule => rule.Rule)]));
    }

    private static Rule Read(string kind, JsonElement item)
    {
        var period = new Period(Day(item.GetProperty("from").GetString()!), item.GetProperty("until").GetString() is { } until ? Day(until) : null);
        var source = new Source(item.GetProperty("clause_id").GetString()!, item.GetProperty("quote").GetString()!);
        decimal Number(string name) => item.GetProperty(name).GetDecimal();

        return kind switch
        {
            "minimum_wage" => new MinimumWageRule("", period, source,
                [.. item.GetProperty("bands").EnumerateArray().Select(band =>
                    new SeniorityBand(band.GetProperty("from_months").GetInt32(), band.GetProperty("hourly_rate").GetDecimal()))]),
            "overtime" => new OvertimeRule("", period, source,
                item.GetProperty("daily_norm_minutes").GetInt32() / 60m,
                [.. item.GetProperty("tiers").EnumerateArray().Select(tier => new OvertimeTier(
                    tier.GetProperty("minutes").ValueKind == JsonValueKind.Null ? null : tier.GetProperty("minutes").GetInt32() / 60m,
                    tier.GetProperty("percent").GetDecimal()))],
                Number("sunday_percent")),
            "inconvenient_hours" => new InconvenientHoursRule("", period, source,
                [.. item.GetProperty("windows").EnumerateArray().Select(window => new TimeWindow(
                    [.. window.GetProperty("days").EnumerateArray().Select(day => Enum.Parse<DayOfWeek>(day.GetString()!))],
                    Time(window.GetProperty("from").GetString()!),
                    Time(window.GetProperty("to").GetString()!),
                    window.GetProperty("amount_per_hour").GetDecimal()))]),
            "pension" => new PensionRule("", period, source, Number("employer_percent"), Number("employee_percent"), item.GetProperty("qualifying_months").GetInt32()),
            "holiday_allowance" => new HolidayAllowanceRule("", period, source, Number("percent")),
            "special_savings" => new SpecialSavingsRule("", period, source, Number("percent")),
            _ => throw new InvalidOperationException($"Unknown kind of rule {kind}."),
        };

        static DateOnly Day(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        static TimeOnly Time(string text) => TimeOnly.ParseExact(text, "HH:mm", CultureInfo.InvariantCulture);
    }
}

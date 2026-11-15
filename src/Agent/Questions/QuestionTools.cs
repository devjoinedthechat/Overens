using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Overens.Rules;

namespace Overens.Agent.Questions;

/// <summary>
/// What the question agent can do: search and read the agreement, look up the published
/// rules, and have pay worked out by the same engine that checks payslips. It cannot change
/// anything. Every result is data for the model, and is kept so the answer can be checked
/// against it.
/// </summary>
public sealed class QuestionTools(RuleSet rules, IClauseIndex index)
{
    public const string GiveAnswer = "give_answer";

    private static readonly JsonSerializerOptions Json = new(RuleJson.Options) { WriteIndented = false };

    public static IReadOnlyList<AIFunctionDeclaration> Declarations { get; } =
    [
        Declare("search_clauses", "Search the agreement for clauses about a topic. Returns the ids and openings of the best matches. Search in Danish, with the words the agreement would use.",
            """{ "type": "object", "properties": { "query": { "type": "string" } }, "required": ["query"], "additionalProperties": false }"""),
        Declare("read_clause", "Read the full text of one clause by its id, exactly as search_clauses gave it, for example \"§ 5, stk. 2\".",
            """{ "type": "object", "properties": { "clause_id": { "type": "string" } }, "required": ["clause_id"], "additionalProperties": false }"""),
        Declare("rules_in_force", "The approved pay rules that apply on a date (YYYY-MM-DD): rates, percentages, time windows, and the clause each comes from. Use this for what a rate is on a given date.",
            """{ "type": "object", "properties": { "date": { "type": "string" } }, "required": ["date"], "additionalProperties": false }"""),
        Declare("calculate_pay", "Work out what shifts should pay under the approved rules. This is the only way to get an amount that is not printed in the agreement. Times are HH:MM; a shift that ends at or before its start time ends the next day. A break is the gap between two shifts.",
            """
            { "type": "object", "properties": {
                "hourly_rate": { "type": "number" },
                "hired_on": { "type": "string" },
                "shifts": { "type": "array", "items": { "type": "object", "properties": {
                  "date": { "type": "string" }, "start": { "type": "string" }, "end": { "type": "string" } },
                  "required": ["date", "start", "end"], "additionalProperties": false } } },
              "required": ["hourly_rate", "hired_on", "shifts"], "additionalProperties": false }
            """),
        Declare(GiveAnswer, "Give the final answer. Call this exactly once, when you are done.",
            """
            { "type": "object", "properties": {
                "answered": { "type": "boolean" },
                "text": { "type": "string" },
                "citations": { "type": "array", "items": { "type": "object", "properties": {
                  "clause_id": { "type": "string" }, "quote": { "type": "string" } },
                  "required": ["clause_id", "quote"], "additionalProperties": false } } },
              "required": ["answered", "text", "citations"], "additionalProperties": false }
            """),
    ];

    /// <summary>
    /// What the two tools that give numbers were asked and what they answered: the only
    /// numbers an answer may use beyond the agreement's own and the question's.
    /// </summary>
    public List<string> Computed { get; } = [];

    /// <summary>Runs one tool call and gives its result as text. A call that cannot be carried out says why; it never throws.</summary>
    public string Run(string name, IDictionary<string, object?>? arguments)
    {
        try
        {
            var input = JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, object?>());
            switch (name)
            {
                case "search_clauses":
                    var hits = index.Search(input.GetProperty("query").GetString() ?? "");
                    return hits.Count == 0 ? "No clause matches. Try other words." : JsonSerializer.Serialize(hits, Json);
                case "read_clause":
                    var id = input.GetProperty("clause_id").GetString() ?? "";
                    return index.Read(id) ?? $"The agreement has no clause \"{id}\".";
                case "rules_in_force":
                    var day = Day(input.GetProperty("date").GetString());
                    // What was asked for is kept with what came back: an answer may name the date it looked up.
                    Computed.Add(input.GetRawText());
                    return Keep(JsonSerializer.Serialize(Listed(rules.InForce(day)), Json));
                case "calculate_pay":
                    var pay = Calculate(input);
                    Computed.Add(input.GetRawText());
                    return Keep(pay);
                default:
                    return $"There is no tool \"{name}\".";
            }
        }
        catch (Exception error) when (error is KeyNotFoundException or FormatException or ArgumentException or InvalidOperationException or JsonException or OverflowException)
        {
            return $"The call could not be carried out: {error.Message}";
        }
    }

    private string Keep(string result)
    {
        Computed.Add(result);
        return result;
    }

    private string Calculate(JsonElement input)
    {
        var shifts = input.GetProperty("shifts").EnumerateArray().Select(shift => new Shift(
            Day(shift.GetProperty("date").GetString()),
            Time(shift.GetProperty("start").GetString()),
            Time(shift.GetProperty("end").GetString()))).ToList();
        if (shifts.Count is 0 or > 62)
        {
            throw new ArgumentException("Give between 1 and 62 shifts.");
        }

        var employee = new Employee("asked-about", Day(input.GetProperty("hired_on").GetString()));
        var timesheet = new Timesheet(employee, shifts.Min(shift => shift.Date), shifts.Max(shift => shift.Date), shifts);
        var expected = PayEngine.Expect(rules, timesheet, input.GetProperty("hourly_rate").GetDecimal());
        var byId = rules.Rules.ToDictionary(rule => rule.Id);
        return JsonSerializer.Serialize(new
        {
            lines = expected.Components.GroupBy(part => part.Line).Select(line => new
            {
                line = line.Key.ToString(),
                amount = line.Sum(part => part.Amount),
                parts = line.Select(part => new
                {
                    minutes = part.Minutes,
                    hours = part.Minutes is { } minutes ? decimal.Round(minutes / 60m, 2) : (decimal?)null,
                    rate_per_hour = part.Minutes is null ? (decimal?)null : part.Rate,
                    percent = part.Basis is null ? (decimal?)null : part.Rate,
                    of_amount = part.Basis,
                    amount = part.Amount,
                    clause = part.RuleId is { } rule ? byId[rule].Source.ClauseId : null,
                }),
            }),
            minimum_wage_applied = expected.Shortfalls.Select(shortfall => new { minimum = shortfall.Value, clause = shortfall.Key.Source.ClauseId }),
        }, Json);
    }

    private static List<Rule> Listed(RulesInForce inForce) =>
        [.. new Rule?[] { inForce.MinimumWage, inForce.Overtime, inForce.InconvenientHours, inForce.Pension, inForce.HolidayAllowance, inForce.SpecialSavings }.OfType<Rule>()];

    private static DateOnly Day(string? text) => DateOnly.ParseExact(text ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static TimeOnly Time(string? text) => TimeOnly.ParseExact(text ?? "", "HH:mm", CultureInfo.InvariantCulture);

    private static AIFunctionDeclaration Declare(string name, string description, string schema) =>
        AIFunctionFactory.CreateDeclaration(name, description, JsonSerializer.Deserialize<JsonElement>(schema));
}

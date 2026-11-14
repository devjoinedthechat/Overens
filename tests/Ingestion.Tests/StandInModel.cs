using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Overens.Documents;
using Overens.Rules;

namespace Overens.Ingestion.Tests;

/// <summary>
/// A model that already knows the answer: it replies to each extraction request with the
/// agreement's true rules, in the shape the schema asks for. A test can spoil its answers
/// before they are sent, to play a model that paraphrases, misreads or invents.
/// </summary>
internal sealed partial class StandInModel(AgreementDocument document, RuleSet truth, Action<string, JsonArray>? spoil = null) : IChatClient
{
    public List<(string Kind, string Prompt, ChatOptions? Options)> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var prompt = messages.Last().Text;
        var kind = TaskKind().Match(prompt).Groups["kind"].Value;
        lock (Requests)
        {
            Requests.Add((kind, prompt, options));
        }

        var rules = new JsonArray([.. truth.Rules.Select(Answer).Where(rule => rule.Kind == kind).Select(rule => rule.Json)]);
        spoil?.Invoke(kind, rules);
        var text = kind == "unreadable" ? "not json" : new JsonObject { ["rules"] = rules }.ToJsonString();
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    private (string Kind, JsonNode Json) Answer(Rule rule)
    {
        // A careful reader cites the clauses around the one it quotes, and the ones it refers to.
        var sections = document.Sections.Concat(document.Protocols).ToList();
        var also = sections
            .Where(section => section.Clauses.Any(clause => clause.Id == rule.Source.ClauseId) || section.Heading is "Arbejdstid" or "Pension")
            .SelectMany(section => section.Clauses.Select(clause => clause.Id))
            .Where(id => id != rule.Source.ClauseId);
        var json = new JsonObject
        {
            ["from"] = rule.InForce.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["until"] = rule.InForce.Until?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["clause_id"] = rule.Source.ClauseId,
            ["quote"] = rule.Source.Quote,
            ["also_from"] = new JsonArray([.. also.Select(id => (JsonNode)id)]),
        };
        switch (rule)
        {
            case MinimumWageRule wage:
                json["increase"] = null;
                json["bands"] = new JsonArray([.. wage.Bands.Select(band => new JsonObject { ["from_months"] = band.FromMonths, ["hourly_rate"] = band.HourlyRate })]);
                return ("minimum_wage", json);
            case OvertimeRule overtime:
                json["daily_norm_minutes"] = (int)(overtime.DailyNormHours * 60);
                json["tiers"] = new JsonArray([.. overtime.Tiers.Select(tier => new JsonObject { ["minutes"] = tier.Hours is { } hours ? (int)(hours * 60) : null, ["percent"] = tier.Percent })]);
                json["sunday_percent"] = overtime.SundayPercent;
                return ("overtime", json);
            case InconvenientHoursRule inconvenient:
                json["windows"] = new JsonArray([.. inconvenient.Windows.Select(window => new JsonObject
                {
                    ["days"] = new JsonArray([.. window.Days.Select(day => (JsonNode)day.ToString())]),
                    ["from"] = window.From.ToString("HH:mm", CultureInfo.InvariantCulture),
                    ["to"] = window.To.ToString("HH:mm", CultureInfo.InvariantCulture),
                    ["amount_per_hour"] = window.AmountPerHour,
                })]);
                return ("inconvenient_hours", json);
            case PensionRule pension:
                json["employer_percent"] = pension.EmployerPercent;
                json["employee_percent"] = pension.EmployeePercent;
                json["qualifying_months"] = pension.QualifyingMonths;
                return ("pension", json);
            case HolidayAllowanceRule holiday:
                json["percent"] = holiday.Percent;
                return ("holiday_allowance", json);
            case SpecialSavingsRule savings:
                json["percent"] = savings.Percent;
                return ("special_savings", json);
            default:
                throw new NotSupportedException();
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    [GeneratedRegex("<task kind=\"(?<kind>[a-z_]+)\">")]
    private static partial Regex TaskKind();
}

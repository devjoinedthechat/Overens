using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Overens.Gateway;

/// <summary>What a span may say. There is no way to attach anything else.</summary>
public enum Field
{
    Tenant,
    Run,
    Agreement,
    Model,
    PromptVersion,
    Status,
    Outcome,
    Clauses,
    RulesProposed,
    RulesFlagged,
    Gaps,
    ModelCalls,
    InputTokens,
    OutputTokens,
    CostMicros,
    Version,
    Findings,
    Steps,
}

/// <summary>
/// Spans for the dashboard: what was done, for which run, how large and how dear. Never what
/// was read or said. A span takes its attributes from <see cref="Field"/> only; a value is a
/// number or a short label such as an id, a model name or a status, and anything that is
/// not shaped like one is withheld. So a clause, a question, an answer or an employee id
/// cannot end up in telemetry by a slip.
///
/// Every span of a run is in one trace, whichever process made it: the trace id is derived
/// from the run's id.
/// </summary>
public static partial class Telemetry
{
    public const string SourceName = "Overens";
    public const string Withheld = "[withheld]";

    private static readonly ActivitySource Source = new(SourceName);

    private static readonly Dictionary<Field, string> Names = new()
    {
        [Field.Tenant] = "overens.tenant",
        [Field.Run] = "overens.run",
        [Field.Agreement] = "overens.agreement",
        [Field.Model] = "gen_ai.response.model",
        [Field.PromptVersion] = "overens.prompt_version",
        [Field.Status] = "overens.status",
        [Field.Outcome] = "overens.outcome",
        [Field.Clauses] = "overens.clauses",
        [Field.RulesProposed] = "overens.rules.proposed",
        [Field.RulesFlagged] = "overens.rules.flagged",
        [Field.Gaps] = "overens.gaps",
        [Field.ModelCalls] = "overens.model.calls",
        [Field.InputTokens] = "gen_ai.usage.input_tokens",
        [Field.OutputTokens] = "gen_ai.usage.output_tokens",
        [Field.CostMicros] = "overens.model.cost_micros",
        [Field.Version] = "overens.rule_set.version",
        [Field.Findings] = "overens.findings",
        [Field.Steps] = "overens.steps",
    };

    /// <summary>Starts a span. With a run id, the span joins that run's trace.</summary>
    public static Activity? Start(string name, string? runId = null)
    {
        if (runId is null)
        {
            return Source.StartActivity(name);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(runId));
        var parent = new ActivityContext(
            ActivityTraceId.CreateFromBytes(hash.AsSpan(0, 16)),
            ActivitySpanId.CreateFromBytes(hash.AsSpan(16, 8)),
            ActivityTraceFlags.Recorded,
            isRemote: true);
        return Source.StartActivity(name, ActivityKind.Internal, parent)?.With(Field.Run, runId);
    }

    public static Activity? With(this Activity? span, Field field, long value)
    {
        span?.SetTag(Names[field], value);
        return span;
    }

    /// <summary>Attaches a label, or a marker that one was withheld when it is not shaped like a label.</summary>
    public static Activity? With(this Activity? span, Field field, string? value)
    {
        if (value is not null)
        {
            // Shaped like a label, and not like a CPR number, which is also letters-digits-and-a-hyphen.
            span?.SetTag(Names[field], Label().IsMatch(value) && RedactingChatClient.Redact(value) == value ? value : Withheld);
        }

        return span;
    }

    /// <summary>Marks the span as failed, with the kind of error and not its message, which may quote what was being read.</summary>
    public static void Failed(this Activity? span, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        span?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
    }

    /// <summary>An id, a model name, a version or a status: letters, digits and a few joining marks, with no spaces.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.:@,-]{1,100}$")]
    private static partial Regex Label();
}

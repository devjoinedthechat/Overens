using Microsoft.Extensions.AI;

namespace Overens.Gateway;

/// <summary>
/// What the model calls made by one piece of work cost: one agreement's extraction, say.
/// It is opened around the work and filled in by every metered call made inside it, on
/// whatever thread, so the caller does not have to pass anything down.
/// </summary>
public sealed class Usage : IDisposable
{
    private static readonly AsyncLocal<Usage?> Current = new();
    private readonly Usage? outer;
    private readonly Lock gate = new();
    private readonly HashSet<string> models = [];

    private Usage(Usage? outer) => this.outer = outer;

    public int Calls { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    /// <summary>Millionths of a dollar, as a whole number so that sums are exact.</summary>
    public long CostMicros { get; private set; }

    /// <summary>The models that answered, which under failover need not be the one asked for.</summary>
    public IReadOnlyCollection<string> Models
    {
        get
        {
            lock (gate)
            {
                return [.. models.Order(StringComparer.Ordinal)];
            }
        }
    }

    public static Usage Begin()
    {
        var usage = new Usage(Current.Value);
        Current.Value = usage;
        return usage;
    }

    internal static void Record(string model, ModelPrice price, UsageDetails? used)
    {
        for (var usage = Current.Value; usage is not null; usage = usage.outer)
        {
            lock (usage.gate)
            {
                usage.Calls++;
                usage.InputTokens += used?.InputTokenCount ?? 0;
                usage.OutputTokens += used?.OutputTokenCount ?? 0;
                usage.CostMicros += (long)Math.Ceiling(price.Micros(used));
                usage.models.Add(model);
            }
        }
    }

    public void Dispose() => Current.Value = outer;
}

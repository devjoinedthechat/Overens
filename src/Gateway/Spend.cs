using Microsoft.Extensions.AI;

namespace Overens.Gateway;

/// <summary>What has been spent on model calls so far. Safe to share between callers.</summary>
public sealed class Spend
{
    private readonly Lock gate = new();

    public decimal Dollars { get; private set; }

    public int Calls { get; private set; }

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public void Add(ModelPrice price, UsageDetails? usage)
    {
        ArgumentNullException.ThrowIfNull(price);
        var cached = usage?.CachedInputTokenCount ?? 0;
        var input = Math.Max(0, (usage?.InputTokenCount ?? 0) - cached);
        var output = usage?.OutputTokenCount ?? 0;
        lock (gate)
        {
            Calls++;
            InputTokens += input + cached;
            OutputTokens += output;
            Dollars += ((input * price.Input) + (cached * price.CacheRead) + (output * price.Output)) / 1_000_000m;
        }
    }
}

public sealed class BudgetExceededException(string message) : Exception(message);

/// <summary>
/// Counts what every call costs and refuses the next one once the cap is reached. A call to a
/// model with no known price is refused too: what cannot be priced cannot be budgeted.
/// </summary>
public sealed class MeteredChatClient(IChatClient inner, Spend spend, decimal capDollars) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var model = options?.ModelId ?? throw new InvalidOperationException("A model call must name its model.");
        if (!Models.Prices.TryGetValue(model, out var price))
        {
            throw new InvalidOperationException($"No price is known for model {model}.");
        }

        if (spend.Dollars >= capDollars)
        {
            throw new BudgetExceededException($"The spending cap of ${capDollars} is reached (${spend.Dollars:0.0000} spent).");
        }

        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        // Priced by the model that answered, which under failover need not be the one asked for.
        var answered = response.ModelId is { } other && Models.Prices.ContainsKey(other) ? other : model;
        spend.Add(Models.Prices[answered], response.Usage);
        Usage.Record(answered, Models.Prices[answered], response.Usage);
        return response;
    }
}

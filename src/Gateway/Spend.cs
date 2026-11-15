using Microsoft.Extensions.AI;

namespace Overens.Gateway;

/// <summary>What has been spent on model calls so far. Safe to share between callers.</summary>
public sealed class Spend
{
    private readonly Lock gate = new();
    private decimal dollars;
    private decimal reserved;
    private int calls;
    private long inputTokens;
    private long outputTokens;

    public decimal Dollars
    {
        get
        {
            lock (gate)
            {
                return dollars;
            }
        }
    }

    public int Calls
    {
        get
        {
            lock (gate)
            {
                return calls;
            }
        }
    }

    public long InputTokens
    {
        get
        {
            lock (gate)
            {
                return inputTokens;
            }
        }
    }

    public long OutputTokens
    {
        get
        {
            lock (gate)
            {
                return outputTokens;
            }
        }
    }

    public void Add(ModelPrice price, UsageDetails? usage)
    {
        ArgumentNullException.ThrowIfNull(price);
        lock (gate)
        {
            calls++;
            inputTokens += usage?.InputTokenCount ?? 0;
            outputTokens += usage?.OutputTokenCount ?? 0;
            dollars += price.Micros(usage) / 1_000_000m;
        }
    }

    /// <summary>
    /// Sets aside what a call may cost at most, unless what is spent and what calls still
    /// under way have set aside already reaches the cap. Calls made at the same moment
    /// therefore cannot all slip under a cap that only one of them fits under.
    /// </summary>
    /// <returns>What is spent so far when the cap is reached, or null when the call may go ahead.</returns>
    internal decimal? Reserve(decimal estimate, decimal capDollars)
    {
        lock (gate)
        {
            if (dollars + reserved >= capDollars)
            {
                return dollars;
            }

            reserved += estimate;
            return null;
        }
    }

    internal void Release(decimal estimate)
    {
        lock (gate)
        {
            reserved -= estimate;
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

        var asked = messages.ToList();
        // The most this call can cost: a token is never shorter than two characters of prose, and the answer never longer than asked for.
        var estimate = ((asked.Sum(message => message.Text.Length) / 2m * price.Input) + ((options.MaxOutputTokens ?? 4096) * price.Output)) / 1_000_000m;
        if (spend.Reserve(estimate, capDollars) is { } spent)
        {
            throw new BudgetExceededException($"The spending cap of ${capDollars} is reached (${spent:0.0000} spent).");
        }

        try
        {
            var response = await base.GetResponseAsync(asked, options, cancellationToken);
            // Priced by the model that answered, which under failover need not be the one asked for.
            var answered = response.ModelId is { } other && Models.Prices.ContainsKey(other) ? other : model;
            spend.Add(Models.Prices[answered], response.Usage);
            Usage.Record(answered, Models.Prices[answered], response.Usage);
            return response;
        }
        finally
        {
            spend.Release(estimate);
        }
    }

    /// <summary>A streamed answer would pass the cap unpriced, so none is given.</summary>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Streamed answers are not metered. Ask for a whole answer.");
}

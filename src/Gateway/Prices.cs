using Microsoft.Extensions.AI;

namespace Overens.Gateway;

/// <summary>US dollars per million tokens.</summary>
public sealed record ModelPrice(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite)
{
    /// <summary>
    /// What one call cost, in millionths of a dollar. The provider reports input as one sum
    /// of what was read fresh, what was read from its cache and what was written to it; each
    /// part has its own price.
    /// </summary>
    public decimal Micros(UsageDetails? usage)
    {
        var read = usage?.CachedInputTokenCount ?? 0;
        var written = usage?.AdditionalCounts?.GetValueOrDefault(CacheWritten) ?? 0;
        var fresh = Math.Max(0, (usage?.InputTokenCount ?? 0) - read - written);
        return (fresh * Input) + (read * CacheRead) + (written * CacheWrite) + ((usage?.OutputTokenCount ?? 0) * Output);
    }

    /// <summary>The name the provider's client gives the count of tokens written to the cache.</summary>
    public const string CacheWritten = "CacheCreationInputTokens";
}

public static class Models
{
    public const string Sonnet = "claude-sonnet-5-5";
    public const string Haiku = "claude-haiku-5-5";

    /// <summary>
    /// List prices as of October 2026. Check them against the provider's pricing page before
    /// relying on a cost figure.
    /// </summary>
    public static IReadOnlyDictionary<string, ModelPrice> Prices { get; } = new Dictionary<string, ModelPrice>
    {
        [Haiku] = new(0.1m, 0.5m, 0.01m, 0.125m),
        [Sonnet] = new(2m, 10m, 0.2m, 2.5m),
    };
}

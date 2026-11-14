namespace Overens.Gateway;

/// <summary>US dollars per million tokens.</summary>
public sealed record ModelPrice(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);

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

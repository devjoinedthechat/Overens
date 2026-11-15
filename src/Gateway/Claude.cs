using Anthropic;
using Microsoft.Extensions.AI;

namespace Overens.Gateway;

/// <summary>
/// Claude, behind the interface every caller uses. Whatever is sent passes through
/// <see cref="RedactingChatClient"/> first, so a CPR number cannot reach the provider.
/// </summary>
public static class Claude
{
    /// <summary>How long a person waits for a model inside a request.</summary>
    public static readonly TimeSpan Interactive = TimeSpan.FromSeconds(20);

    /// <summary>How long the pipeline waits for a model to read an agreement.</summary>
    public static readonly TimeSpan Background = TimeSpan.FromMinutes(5);

    /// <summary>
    /// What stands between every caller and a provider, outermost first: redaction, so nothing
    /// below it ever holds a CPR number; then failover to the provider's other model; then a
    /// time limit on each model's turn.
    /// </summary>
    /// <param name="provider">The provider's own client.</param>
    /// <param name="limit">How long one model has to answer.</param>
    /// <param name="failover">False when the answer must come from the model asked, as when answers are recorded for the evals.</param>
    public static IChatClient Wrap(IChatClient provider, TimeSpan limit, bool failover = true)
    {
        IChatClient timed = new TimeoutChatClient(provider, limit);
        return new RedactingChatClient(failover ? new FailoverChatClient(timed, FailoverChatClient.Claude) : timed);
    }

    /// <summary>
    /// Text the provider may keep for a few minutes and read again at a tenth of the price:
    /// the long part of a prompt that several calls share, placed before what differs.
    /// </summary>
    public static TextContent Cacheable(string text) => new TextContent(text).WithCacheControl(Anthropic.Models.Messages.Ttl.Ttl5m);

    public static IChatClient Create(string apiKey, TimeSpan limit, bool failover = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return Wrap(new AnthropicClient { ApiKey = apiKey }.AsIChatClient(), limit, failover);
    }
}

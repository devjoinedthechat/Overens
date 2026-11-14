using Anthropic;
using Microsoft.Extensions.AI;

namespace Overens.Gateway;

/// <summary>
/// Claude, behind the interface every caller uses. Whatever is sent passes through
/// <see cref="RedactingChatClient"/> first, so a CPR number cannot reach the provider.
/// </summary>
public static class Claude
{
    /// <summary>
    /// What stands between every caller and a provider, outermost first: redaction, so nothing
    /// below it ever holds a CPR number; then failover to the provider's other model.
    /// </summary>
    public static IChatClient Wrap(IChatClient provider) => new RedactingChatClient(new FailoverChatClient(provider, FailoverChatClient.Claude));

    public static IChatClient Create(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return Wrap(new AnthropicClient { ApiKey = apiKey }.AsIChatClient());
    }
}

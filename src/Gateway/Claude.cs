using Anthropic;
using Microsoft.Extensions.AI;

namespace Overens.Gateway;

/// <summary>
/// Claude, behind the interface every caller uses. Whatever is sent passes through
/// <see cref="RedactingChatClient"/> first, so a CPR number cannot reach the provider.
/// </summary>
public static class Claude
{
    public static IChatClient Create(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return new RedactingChatClient(new AnthropicClient { ApiKey = apiKey }.AsIChatClient());
    }
}

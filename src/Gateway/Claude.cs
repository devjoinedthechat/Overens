using Anthropic;
using Microsoft.Extensions.AI;

namespace Overens.Gateway;

/// <summary>Claude, behind the interface every caller uses.</summary>
public static class Claude
{
    public static IChatClient Create(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return new AnthropicClient { ApiKey = apiKey }.AsIChatClient();
    }
}

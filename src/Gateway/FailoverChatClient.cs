using System.Net;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;

namespace Overens.Gateway;

/// <summary>
/// Asks a second model when the first cannot be reached: the provider is overloaded, rate
/// limited or timing out. The answer says which model gave it. A request the provider
/// refuses as wrong (a bad schema, a bad key) is not sent again to another model: it would
/// be refused there too, or worse, accepted.
///
/// Both models are the same provider's, so this covers one model being unavailable. It does
/// not cover the provider being down.
/// </summary>
public sealed class FailoverChatClient(IChatClient inner, IReadOnlyDictionary<string, string> fallbacks) : DelegatingChatClient(inner)
{
    /// <summary>Sonnet falls back to Haiku and Haiku to Sonnet.</summary>
    public static IReadOnlyDictionary<string, string> Claude { get; } = new Dictionary<string, string>
    {
        [Models.Sonnet] = Models.Haiku,
        [Models.Haiku] = Models.Sonnet,
    };

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var asked = messages.ToList();
        try
        {
            return await base.GetResponseAsync(asked, options, cancellationToken);
        }
        catch (Exception unavailable) when (IsUnavailable(unavailable, cancellationToken) && options?.ModelId is { } model && fallbacks.TryGetValue(model, out var fallback))
        {
            var second = options.Clone();
            second.ModelId = fallback;
            var response = await base.GetResponseAsync(asked, second, cancellationToken);
            // Said plainly, so that whoever prices or records the call knows which model answered.
            response.ModelId = fallback;
            return response;
        }
    }

    /// <summary>Whether a failure means "not now" and not "not like this".</summary>
    public static bool IsUnavailable(Exception error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            // A timeout, unless the caller itself gave up.
            OperationCanceledException => !cancellationToken.IsCancellationRequested,
            HttpRequestException or TimeoutException or IOException => true,
            // The provider's client: the connection failed, or the provider answered "too many", "too slow" or "not now".
            AnthropicIOException => true,
            AnthropicApiException answered => answered.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)answered.StatusCode >= 500,
            _ => false,
        };
    }
}

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
        if (error is OperationCanceledException)
        {
            // A timeout, unless the caller itself gave up.
            return !cancellationToken.IsCancellationRequested;
        }

        if (error is HttpRequestException or TimeoutException or IOException)
        {
            return true;
        }

        // The provider's own errors carry the status in their type's name: rate limit, overloaded, 5xx.
        var name = error.GetType().Name;
        return name.Contains("RateLimit", StringComparison.Ordinal) || name.Contains("5xx", StringComparison.Ordinal)
            || name.Contains("Overloaded", StringComparison.Ordinal) || name.Contains("ServiceUnavailable", StringComparison.Ordinal)
            || name.Contains("InternalServer", StringComparison.Ordinal);
    }
}

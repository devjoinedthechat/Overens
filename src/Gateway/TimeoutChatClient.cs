using Microsoft.Extensions.AI;

namespace Overens.Gateway;

/// <summary>
/// Gives a model a fixed time to answer, the provider client's own retries included. A call
/// that takes longer ends as a <see cref="TimeoutException"/>, which is a failure like any
/// other to whoever asked: it is not mistaken for the caller having given up.
/// </summary>
public sealed class TimeoutChatClient(IChatClient inner, TimeSpan limit) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(limit);
        try
        {
            return await base.GetResponseAsync(messages, options, timer.Token);
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The model did not answer within {limit.TotalSeconds:0} seconds.");
        }
    }
}

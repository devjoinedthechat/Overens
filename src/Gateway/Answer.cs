using Microsoft.Extensions.AI;

namespace Overens.Gateway;

/// <summary>
/// The model stopped before its answer was whole: it ran out of the tokens it was allowed,
/// or it declined. Asking again the same way would end the same way.
/// </summary>
public sealed class IncompleteAnswerException : Exception
{
    public IncompleteAnswerException(string message)
        : base(message)
    {
    }

    public IncompleteAnswerException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public IncompleteAnswerException()
    {
    }
}

public static class Answer
{
    /// <summary>
    /// Refuses an answer the model did not finish. Without this, half an answer shows up
    /// later as text that cannot be read, and looks like passing trouble worth trying again.
    /// </summary>
    public static ChatResponse RequireComplete(this ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.FinishReason == ChatFinishReason.Length)
        {
            throw new IncompleteAnswerException("The model's answer was cut off: it was longer than the answer allowed.");
        }

        if (response.FinishReason == ChatFinishReason.ContentFilter)
        {
            throw new IncompleteAnswerException("The model declined to answer.");
        }

        return response;
    }
}

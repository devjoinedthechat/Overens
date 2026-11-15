using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Overens.Gateway;

/// <summary>
/// Takes anything shaped like a CPR number out of what is sent to a model, whoever is
/// sending and whatever it is in: a question, a clause, a tool result. It is the outermost
/// layer of every client made here, so no caller can forget it and nothing below it ever
/// holds a CPR number.
///
/// A CPR number is six digits of a date and four more, with or without a hyphen or spaces
/// between, the date sometimes written with dots. Ten digits in a row that are something
/// else are taken out too: nothing sent to a model here needs one.
/// </summary>
public sealed partial class RedactingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public const string Removed = "[CPR-nummer fjernet]";

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync([.. messages.Select(Redact)], options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync([.. messages.Select(Redact)], options, cancellationToken);

    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PersonalNumber().Replace(text, Removed);
    }

    private static ChatMessage Redact(ChatMessage message)
    {
        if (!message.Contents.Any(content => content switch
        {
            TextContent text => PersonalNumber().IsMatch(text.Text),
            FunctionResultContent { Result: { } result } => PersonalNumber().IsMatch(Written(result)),
            _ => false,
        }))
        {
            return message;
        }

        // Everything else about the message stays as it was: who wrote it, and what the caller attached to it.
        return new(message.Role, [.. message.Contents.Select(content => content switch
        {
            TextContent text => new TextContent(Redact(text.Text)) { AdditionalProperties = text.AdditionalProperties, Annotations = text.Annotations },
            FunctionResultContent { Result: { } result } call when PersonalNumber().IsMatch(Written(result)) =>
                new FunctionResultContent(call.CallId, Redact(Written(result))) { AdditionalProperties = call.AdditionalProperties },
            _ => content,
        })])
        {
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            AdditionalProperties = message.AdditionalProperties,
        };
    }

    /// <summary>A tool's result as the provider will receive it: text as it is, anything else as JSON.</summary>
    private static string Written(object result) => result as string ?? JsonSerializer.Serialize(result);

    [GeneratedRegex(@"(?<!\d)(?:\d{6}|\d{2}\.\d{2}\.\d{2})\s?[- ]?\s?\d{4}(?!\d)")]
    private static partial Regex PersonalNumber();
}

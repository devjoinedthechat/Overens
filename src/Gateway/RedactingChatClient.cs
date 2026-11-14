using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Overens.Gateway;

/// <summary>
/// Takes anything shaped like a CPR number out of what is sent to a model, whoever is
/// sending and whatever it is in: a question, a clause, a tool result. It stands closest to
/// the provider, so no caller can forget it.
///
/// A CPR number is six digits of a date and four more, with or without a hyphen or a space
/// between. Ten digits in a row that are something else are taken out too: nothing sent to
/// a model here needs one.
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
            FunctionResultContent result => result.Result is string value && PersonalNumber().IsMatch(value),
            _ => false,
        }))
        {
            return message;
        }

        return new(message.Role, [.. message.Contents.Select(content => content switch
        {
            TextContent text => new TextContent(Redact(text.Text)),
            FunctionResultContent { Result: string value } result => new FunctionResultContent(result.CallId, Redact(value)),
            _ => content,
        })]);
    }

    [GeneratedRegex(@"(?<!\d)\d{6}[- ]?\d{4}(?!\d)")]
    private static partial Regex PersonalNumber();
}

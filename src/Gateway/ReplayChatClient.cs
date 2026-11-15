using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Overens.Gateway;

/// <summary>
/// Keeps a model's answers on disk, one file per distinct request, so that evals can run again
/// without calling the model. A recorded answer is only ever returned for the exact request
/// that produced it: change the prompt, the model or the schema, and it must be recorded again.
/// </summary>
public sealed class ReplayChatClient : DelegatingChatClient
{
    private readonly string directory;
    private readonly bool record;

    /// <param name="inner">The real model, or null to replay only.</param>
    public ReplayChatClient(IChatClient? inner, string directory)
        : base(inner ?? new NoModel())
    {
        this.directory = directory;
        record = inner is not null;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var asked = messages.ToList();
        var file = Path.Combine(directory, $"{Key(asked, options)}.json");
        if (File.Exists(file))
        {
            var kept = JsonSerializer.Deserialize<Kept>(await File.ReadAllTextAsync(file, cancellationToken))!;
            var contents = new List<AIContent>();
            if (kept.Text.Length > 0)
            {
                contents.Add(new TextContent(kept.Text));
            }

            contents.AddRange((kept.Calls ?? []).Select(call => new FunctionCallContent(
                call.Id, call.Name, JsonSerializer.Deserialize<Dictionary<string, object?>>(call.Arguments))));
            return new(new ChatMessage(ChatRole.Assistant, contents))
            {
                ModelId = kept.Model,
                FinishReason = kept.Finish is { } ended ? new ChatFinishReason(ended) : null,
            };
        }

        if (!record)
        {
            throw new InvalidOperationException(
                $"No recorded answer for this request ({Path.GetFileName(file)}). Record the evals again with a model key.");
        }

        var response = await base.GetResponseAsync(asked, options, cancellationToken);
        // A recording is filed under the model asked. An answer another model gave must not be kept as its own.
        if (response.ModelId is { } answered && options?.ModelId is { } wanted && !answered.StartsWith(wanted, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{wanted} was asked and {answered} answered. The answer is not recorded.");
        }

        Directory.CreateDirectory(directory);
        // Only the answer is kept, and how it ended when that was not the ordinary way. Token counts and anything else about the account stay out of the file.
        var finish = response.FinishReason is { } reason && reason != ChatFinishReason.Stop && reason != ChatFinishReason.ToolCalls ? reason.Value : null;
        // Written beside the file and moved into place, so a recording is never half a file.
        var draft = $"{file}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(
            draft,
            JsonSerializer.Serialize(new Kept(options?.ModelId ?? "", response.Text, Calls(response), finish), Indented) + "\n",
            cancellationToken);
        File.Move(draft, file, overwrite: true);
        return response;
    }

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Key(List<ChatMessage> messages, ChatOptions? options)
    {
        var request = new StringBuilder(options?.ModelId).Append('\n');
        if (options?.ResponseFormat is ChatResponseFormatJson { Schema: { } schema })
        {
            request.Append(schema.GetRawText()).Append('\n');
        }

        foreach (var tool in options?.Tools?.OfType<AIFunctionDeclaration>() ?? [])
        {
            request.Append("tool ").Append(tool.Name).Append('\n').Append(tool.Description).Append('\n').Append(tool.JsonSchema.GetRawText()).Append('\n');
        }

        foreach (var message in messages)
        {
            request.Append(message.Role.Value).Append('\n').Append(message.Text).Append('\n');
            // A turn in a tool loop is told apart by the calls made and the results returned so far.
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        request.Append("call ").Append(call.Name).Append(' ').Append(JsonSerializer.Serialize(call.Arguments)).Append('\n');
                        break;
                    case FunctionResultContent result:
                        request.Append("result ").Append(result.Result?.ToString()).Append('\n');
                        break;
                    default:
                        break;
                }
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.ToString())))[..32];
    }

    private static List<KeptCall>? Calls(ChatResponse response)
    {
        var calls = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .Select(call => new KeptCall(call.CallId, call.Name, JsonSerializer.Serialize(call.Arguments)))
            .ToList();
        return calls.Count == 0 ? null : calls;
    }

    private sealed record Kept(string Model, string Text, List<KeptCall>? Calls = null, string? Finish = null);

    private sealed record KeptCall(string Id, string Name, string Arguments);

    private sealed class NoModel : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("There is no model behind this client.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("There is no model behind this client.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

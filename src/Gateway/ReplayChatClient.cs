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
            return new(new ChatMessage(ChatRole.Assistant, contents)) { ModelId = kept.Model };
        }

        if (!record)
        {
            throw new InvalidOperationException(
                $"No recorded answer for this request ({Path.GetFileName(file)}). Record the evals again with a model key.");
        }

        var response = await base.GetResponseAsync(asked, options, cancellationToken);
        Directory.CreateDirectory(directory);
        // Only the answer is kept. Token counts and anything else about the account stay out of the file.
        await File.WriteAllTextAsync(
            file,
            JsonSerializer.Serialize(new Kept(options?.ModelId ?? "", response.Text, Calls(response)), Indented) + "\n",
            cancellationToken);
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

    private sealed record Kept(string Model, string Text, List<KeptCall>? Calls = null);

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

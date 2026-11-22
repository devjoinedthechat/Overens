using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Overens.Evals;

/// <summary>
/// Asks a model through Claude Code's print mode (<c>claude -p</c>) in place of the API. It
/// is for evals only, and only for a single question with a strict answer schema, which is
/// what extraction asks.
///
/// It is not the same road to the model as the API. Claude Code adds a little of its own
/// around the request, and the answer's length is not limited from here. The system prompt,
/// the agreement, the task and the schema are the same. Answers recorded this way are kept
/// apart from those recorded through the API.
/// </summary>
public sealed class ClaudeCodeChatClient : IChatClient
{
    /// <summary>The program to run: <c>CLAUDE_CODE_BIN</c>, or <c>claude</c> from the path.</summary>
    private static readonly string Program = Environment.GetEnvironmentVariable("CLAUDE_CODE_BIN") is { Length: > 0 } set ? set : "claude";

    /// <summary>Run from an empty directory, so that no project's instructions or notes are read into the request.</summary>
    private readonly string emptyDirectory = Directory.CreateTempSubdirectory("overens-evals-").FullName;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var asked = messages.ToList();
        if (asked.Any(message => message.Role != ChatRole.System && message.Role != ChatRole.User) || options?.Tools is { Count: > 0 })
        {
            throw new NotSupportedException("Only a single question without tools can be asked through Claude Code.");
        }

        var model = options?.ModelId ?? throw new ArgumentException("A model must be named.", nameof(options));
        var schema = (options.ResponseFormat as ChatResponseFormatJson)?.Schema ?? throw new ArgumentException("An answer schema must be given.", nameof(options));
        var system = string.Join("\n\n", asked.Where(message => message.Role == ChatRole.System).Select(message => message.Text));
        var question = string.Join("\n\n", asked.Where(message => message.Role == ChatRole.User).Select(message => message.Text));

        // The provider now and then refuses for a moment, or the plan's limit is near. Twice more, then give up.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AskAsync(model, system, question, schema.GetRawText(), cancellationToken);
            }
            catch (InvalidOperationException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(20 * attempt), cancellationToken);
            }
        }
    }

    private async Task<ChatResponse> AskAsync(string model, string system, string question, string schema, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Program)
        {
            WorkingDirectory = emptyDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "--print", "--model", model, "--tools", "", "--system-prompt", system, "--json-schema", schema,
            "--output-format", "json", "--setting-sources", "", "--no-session-persistence",
        })
        {
            start.ArgumentList.Add(argument);
        }

        // Whoever holds this client may close it between passes, which clears the directory.
        Directory.CreateDirectory(emptyDirectory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{Program} could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        // The question goes in on standard input: an agreement is too long for a command line.
        await process.StandardInput.WriteAsync(question.AsMemory(), cancellationToken);
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken);

        JsonDocument answer;
        try
        {
            answer = JsonDocument.Parse(await output);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Claude Code gave no answer that can be read (exit {process.ExitCode}): {Short(await errors)}");
        }

        using (answer)
        {
            var root = answer.RootElement;
            if (root.GetProperty("is_error").GetBoolean() || !root.TryGetProperty("structured_output", out var structured) || structured.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Claude Code did not answer in the schema: {Short(root.TryGetProperty("result", out var said) ? said.ToString() : "")}");
            }

            // An answer is filed under the model asked, so that model must have taken part in giving it.
            if (!root.TryGetProperty("modelUsage", out var used) || !used.EnumerateObject().Any(by => by.Name.StartsWith(model, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"{model} was asked, and the answer does not say it answered.");
            }

            return new(new ChatMessage(ChatRole.Assistant, structured.GetRawText())) { ModelId = model, FinishReason = ChatFinishReason.Stop };
        }
    }

    private static string Short(string text) => text.Length > 300 ? text[..300] + "…" : text;

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        try
        {
            Directory.Delete(emptyDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory left behind is the system's to clear.
        }
    }
}

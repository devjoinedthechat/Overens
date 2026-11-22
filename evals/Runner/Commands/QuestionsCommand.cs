using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Overens.Agent;
using Overens.Agent.Questions;
using Overens.Gateway;
using Overens.Ingestion.Extraction;

namespace Overens.Evals.Commands;

/// <summary>Questions with known answers, asked of the question agent: <c>--questions</c>.</summary>
internal static class QuestionsCommand
{
    public static async Task<int> RunAsync(Run run)
    {
        var spend = run.Spend;
        var args = run.Args;
        var modelId = run.ModelId;
        var only = run.Only;
        var record = run.Record;
        var live = run.Live;
        // Questions with known answers, asked of the question agent with the true rules and the agreement's clauses.
        using var asked = new ReplayChatClient(live, Path.Combine("evals", "recordings", $"questions-{QuestionAgent.Version}", modelId));
        var answers = await Questions.RunAsync(asked, modelId, only);
        foreach (var answer in answers.Where(answer => answer.Verdict != "correct" || args.Contains("--show")))
        {
            Console.WriteLine($"\n{answer.Agreement} {answer.Id} ({answer.Kind}): {answer.Verdict}\n  {answer.Text}");
            foreach (var step in answer.Trace)
            {
                Console.WriteLine($"    {(step.Length > 220 ? step[..220] + "…" : step)}");
            }
        }

        Console.WriteLine($"\nModel {modelId}, question prompt {QuestionAgent.Version}");
        foreach (var kind in answers.GroupBy(answer => answer.Kind))
        {
            Console.WriteLine($"{kind.Key,-13} {kind.Count(answer => answer.Verdict == "correct")} of {kind.Count()} correct  ({string.Join(", ", kind.Where(answer => answer.Verdict != "correct").GroupBy(answer => answer.Verdict).Select(verdict => $"{verdict.Count()} {verdict.Key}"))})");
        }

        Console.WriteLine(record ? $"Spent ${spend.Dollars:0.0000} in {spend.Calls} calls ({spend.InputTokens} tokens in, {spend.OutputTokens} out)." : "Replayed from recordings. Nothing spent.");
        if (args.Contains("--save") && only.Length == 0)
        {
            var file = Path.Combine("docs", "eval-report", "results", $"questions-{QuestionAgent.Version}", $"{modelId}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(answers, EvalResults.Json) + "\n");
        }

        return 0;
    }
}

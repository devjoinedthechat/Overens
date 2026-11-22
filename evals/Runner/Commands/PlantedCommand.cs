using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Overens.Agent;
using Overens.Agent.Questions;
using Overens.Gateway;
using Overens.Ingestion.Extraction;

namespace Overens.Evals.Commands;

/// <summary>Mistakes planted in correct answers, and what the checks make of them: <c>--planted</c>.</summary>
internal static class PlantedCommand
{
    public static async Task<int> RunAsync(Run run)
    {
        var args = run.Args;
        var modelId = run.ModelId;
        var only = run.Only;
        var live = run.Live;
        var recordings = run.Recordings;
        // What the model-free checks make of wrong answers: every recorded answer spoiled one way at a time.
        using var answers = new ReplayChatClient(live, recordings);
        var planted = await PlantedMistakes.RunAsync(answers, modelId, spec => only.Length == 0 || spec.Id == only);
        Console.WriteLine($"Model {modelId}, extraction prompt {ExtractionPrompt.Version}\n");
        Console.WriteLine($"{"mistake",-36} {"planted",8} {"stopped",8} {"reported",9} {"silent",7} {"no change",10}");
        foreach (var group in PlantedMistakes.Groups)
        {
            foreach (var total in planted.Totals.Where(total => total.Group == group).Append(planted.Total(group) with { Mistake = $"= {group}" }))
            {
                Console.WriteLine($"{total.Mistake,-36} {total.Planted,8} {total.Stopped,8} {total.Reported,9} {total.Silent,7} {total.NoChange,10}");
            }

            Console.WriteLine();
        }

        var all = planted.Total();
        Console.WriteLine($"{"= all",-36} {all.Planted,8} {all.Stopped,8} {all.Reported,9} {all.Silent,7} {all.NoChange,10}");
        var silent = planted.NotStopped.Where(outcome => outcome.Outcome == "silent").ToList();
        Console.WriteLine($"\nOf {silent.Count} silent mistakes, {silent.Count(outcome => outcome.PayslipsWrong > 0)} made the pay check judge a payslip wrongly, and {silent.Count(outcome => outcome.PayslipsWrong == 0 && outcome.PayslipsNotChecked > 0)} only left lines unchecked.");
        Console.WriteLine("Replayed from recordings. Nothing spent.");
        if (args.Contains("--save") && only.Length == 0)
        {
            var file = Path.Combine("docs", "eval-report", "results", $"planted-{ExtractionPrompt.Version}", $"{modelId}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(planted, EvalResults.Json) + "\n");
            Console.WriteLine($"Saved {file}");
        }

        return 0;
    }
}

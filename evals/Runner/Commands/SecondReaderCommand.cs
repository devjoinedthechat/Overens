using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Overens.Agent;
using Overens.Agent.Questions;
using Overens.Gateway;
using Overens.Ingestion.Extraction;

namespace Overens.Evals.Commands;

/// <summary>Each rule of one model held against a second model's reading: <c>--second-reader</c>.</summary>
internal static class SecondReaderCommand
{
    public static async Task<int> RunAsync(Run run)
    {
        var args = run.Args;
        var modelId = run.ModelId;
        string Option(string name, string fallback) => run.Option(name, fallback);
        // Each rule of the first model held against a second model's reading of the same agreement.
        var second = Option("--second", modelId == Models.Sonnet ? Models.Haiku : Models.Sonnet);
        var paired = await SecondReaderEval.RunAsync(modelId, second, firstPasses: modelId == Models.Sonnet ? 3 : 5, secondPasses: second == Models.Sonnet ? 3 : 5);
        Console.WriteLine($"{modelId} read first, {second} second, extraction prompt {ExtractionPrompt.Version}\n");
        foreach (var set in paired.Agreements.Select(agreement => agreement.Set).Distinct())
        {
            var (alone, together) = (paired.Total(set, false), paired.Total(set, true));
            Console.WriteLine($"{set,-12} wrong and unflagged {alone.WrongAndUnflagged} -> {together.WrongAndUnflagged}   right but flagged {alone.RightButFlagged} -> {together.RightButFlagged}   of {alone.Proposed} proposed");
        }

        foreach (var agreement in paired.Agreements.Where(agreement => agreement.Alone != agreement.WithSecond))
        {
            Console.WriteLine($"  {agreement.Agreement,-18} wrong-unflagged {agreement.Alone.WrongAndUnflagged}->{agreement.WithSecond.WrongAndUnflagged}  right-flagged {agreement.Alone.RightButFlagged}->{agreement.WithSecond.RightButFlagged}  payslips misjudged {agreement.PayAlone.MissedErrors + agreement.PayAlone.FalseAlarms}->{agreement.PayWithSecond.MissedErrors + agreement.PayWithSecond.FalseAlarms}");
        }

        Console.WriteLine($"\nRepeat passes paired, generator agreements: correct rules flagged only by the second reading, per pairing: {string.Join(' ', paired.Pairs.Select(pair => pair.RightButFlagged))} (of about {(paired.Pairs.Count > 0 ? paired.Pairs[0].Rules : 0)} rules)");
        if (args.Contains("--save"))
        {
            var file = Path.Combine("docs", "eval-report", "results", $"second-reader-{ExtractionPrompt.Version}", $"{modelId}-then-{second}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(paired, EvalResults.Json) + "\n");
            Console.WriteLine($"Saved {file}");
        }

        return 0;
    }
}

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Overens.Agent;
using Overens.Agent.Questions;
using Overens.Gateway;
using Overens.Ingestion.Extraction;

namespace Overens.Evals.Commands;

/// <summary>The same extraction asked several times: <c>--variance N</c>.</summary>
internal static class VarianceCommand
{
    public static async Task<int> RunAsync(Run run)
    {
        var args = run.Args;
        var modelId = run.ModelId;
        var only = run.Only;
        var live = run.Live;
        string Option(string name, string fallback) => run.Option(name, fallback);
        var passes = int.Parse(Option("--variance", "1"), CultureInfo.InvariantCulture);
        // The same extraction asked several times, to see how much the answers differ from one time to the next.
        var variance = await Variance.RunAsync(live, modelId, passes, only: spec => only.Length == 0 || spec.Id == only);
        Console.WriteLine($"Model {modelId}, extraction prompt {ExtractionPrompt.Version}, {passes} passes\n");
        foreach (var pass in variance.Passes)
        {
            Console.WriteLine($"pass {pass.Pass}: " + string.Join("  ", pass.Rules.Select(set => $"{set.Key} {set.Value.Correct} of {set.Value.Expected} found, {set.Value.Proposed} proposed, {set.Value.Flagged} flagged, {set.Value.WrongAndUnflagged} wrong and unflagged, payslips {pass.Pay[set.Key].Right} of {pass.Pay[set.Key].Cases};")));
        }

        Console.WriteLine();
        foreach (var agreement in variance.Agreements.Where(agreement => !agreement.SameEveryPass))
        {
            Console.WriteLine($"{agreement.Agreement,-18} found {string.Join('/', agreement.Found)} of {agreement.Expected}  proposed {string.Join('/', agreement.Proposed)}  flagged {string.Join('/', agreement.Flagged)}  wrong-unflagged {string.Join('/', agreement.WrongAndUnflagged)}  payslips right {string.Join('/', agreement.PayslipsRight)} of {agreement.Payslips}");
        }

        Console.WriteLine($"\n{variance.Agreements.Count(agreement => agreement.SameEveryPass)} of {variance.Agreements.Count} agreements came out the same in every pass.");
        if (args.Contains("--save") && only.Length == 0)
        {
            var file = Path.Combine("docs", "eval-report", "results", $"variance-{ExtractionPrompt.Version}", $"{modelId}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(variance, EvalResults.Json) + "\n");
            Console.WriteLine($"Saved {file}");
        }

        return 0;
    }
}

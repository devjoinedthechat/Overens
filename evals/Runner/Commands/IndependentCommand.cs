using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Overens.Agent;
using Overens.Agent.Questions;
using Overens.Gateway;
using Overens.Ingestion.Extraction;

namespace Overens.Evals.Commands;

/// <summary>The agreements written by hand: <c>--independent</c>.</summary>
internal static class IndependentCommand
{
    public static async Task<int> RunAsync(Run run)
    {
        var args = run.Args;
        var modelId = run.ModelId;
        var live = run.Live;
        // Agreements written by hand, by writers who saw nothing of the generator, the prompt or the checks.
        using var reading = new ReplayChatClient(live, Independent.Recordings(modelId));
        var independent = await Independent.RunAsync(reading, modelId);
        Console.WriteLine($"Model {modelId}, extraction prompt {ExtractionPrompt.Version}");
        foreach (var agreement in independent.Agreements)
        {
            var total = agreement.Kinds.Aggregate(new KindScore("all", 0, 0, 0, 0, 0, 0), KindScore.Add);
            Console.WriteLine($"\n{agreement.Agreement}: {total.Correct} of {total.Expected} rules found, {total.Proposed} proposed, {total.Flagged} flagged, {total.WrongAndUnflagged} wrong and unflagged, {total.RightButFlagged} right but flagged. Payslips {agreement.Pay.Right} of {agreement.Pay.Cases} right, {agreement.Pay.MissedErrors} missed, {agreement.Pay.FalseAlarms} false alarms, {agreement.Pay.NotChecked} not checked.");
            foreach (var kind in agreement.Kinds.Where(kind => kind.Correct != kind.Expected || kind.Proposed != kind.Expected || kind.Flagged > 0))
            {
                Console.WriteLine($"  {kind.Kind,-20} expected {kind.Expected}  proposed {kind.Proposed}  correct {kind.Correct}  flagged {kind.Flagged}  wrong-unflagged {kind.WrongAndUnflagged}  right-flagged {kind.RightButFlagged}");
            }

            foreach (var problem in agreement.Problems.Concat(agreement.Gaps.Select(gap => $"gap in {gap}")))
            {
                Console.WriteLine($"  ! {problem}");
            }
        }

        if (args.Contains("--save"))
        {
            var file = Path.Combine("docs", "eval-report", "results", $"independent-{ExtractionPrompt.Version}", $"{modelId}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(independent, EvalResults.Json) + "\n");
            Console.WriteLine($"\nSaved {file}");
        }

        return 0;
    }
}

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Overens.Agent;
using Overens.Agent.Questions;
using Overens.Gateway;
using Overens.Ingestion.Extraction;

namespace Overens.Evals.Commands;

/// <summary>Extraction on every generated agreement, scored against the rules each one really states. The default.</summary>
internal static class ExtractCommand
{
    public static async Task<int> RunAsync(Run run)
    {
        var spend = run.Spend;
        var args = run.Args;
        var modelId = run.ModelId;
        var only = run.Only;
        var record = run.Record;
        var live = run.Live;
        var recordings = run.Recordings;

        using var model = new ReplayChatClient(live, recordings);
        var results = await Evals.RunAsync(model, modelId, spec => only.Length == 0 || spec.Id == only);
        foreach (var agreement in results.Agreements)
        {
            Console.WriteLine($"\n{agreement.Agreement} ({agreement.Set})");
            foreach (var score in agreement.Kinds.Where(score => score.Correct != score.Expected || score.Proposed != score.Expected || score.Flagged > 0))
            {
                Console.WriteLine($"  {score.Kind,-20} expected {score.Expected}  proposed {score.Proposed}  correct {score.Correct}  flagged {score.Flagged}  wrong-unflagged {score.WrongAndUnflagged}  right-flagged {score.RightButFlagged}");
            }

            foreach (var problem in agreement.Problems.Concat(agreement.Gaps.Select(gap => $"gap in {gap}")))
            {
                Console.WriteLine($"  ! {problem}");
            }

            Console.WriteLine($"  payslips: {agreement.Pay.Right} of {agreement.Pay.Cases} right, {agreement.Pay.MissedErrors} with a missed error, {agreement.Pay.FalseAlarms} false alarms, {agreement.Pay.NotChecked} not checked");
        }

        Console.WriteLine($"\nModel {modelId}, prompt {ExtractionPrompt.Version}");
        foreach (var set in results.Agreements.Select(agreement => agreement.Set).Distinct())
        {
            var (rules, pay) = (results.Total(set), results.Pay(set));
            Console.WriteLine($"{set,-10} rules: {rules.Correct} of {rules.Expected} found, {rules.Proposed} proposed, {rules.Flagged} flagged, {rules.WrongAndUnflagged} wrong and unflagged, {rules.RightButFlagged} right but flagged.  payslips: {pay.Right} of {pay.Cases} right, {pay.NotChecked} not checked.");
        }

        Console.WriteLine(record ? $"Spent ${spend.Dollars:0.0000} in {spend.Calls} calls ({spend.InputTokens} tokens in, {spend.OutputTokens} out)." : "Replayed from recordings. Nothing spent.");
        if (args.Contains("--save") && only.Length == 0)
        {
            var file = Path.Combine("docs", "eval-report", "results", ExtractionPrompt.Version, $"{modelId}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(results, EvalResults.Json) + "\n");
            Console.WriteLine($"Saved {file}");
        }

        return 0;

    }
}

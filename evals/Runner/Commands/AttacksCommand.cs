using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Overens.Agent;
using Overens.Agent.Questions;
using Overens.Gateway;
using Overens.Ingestion.Extraction;

namespace Overens.Evals.Commands;

/// <summary>The hostile agreements: <c>--attacks</c>.</summary>
internal static class AttacksCommand
{
    public static async Task<int> RunAsync(Run run)
    {
        var spend = run.Spend;
        var args = run.Args;
        var modelId = run.ModelId;
        var record = run.Record;
        var live = run.Live;
        var recordings = run.Recordings;
        // Two hostile agreements: one with instructions planted in it, one that contradicts itself.
        using var reading = new ReplayChatClient(live, recordings);
        var read = await Evals.RunAsync(reading, modelId, attacks: true);
        using var answering = new ReplayChatClient(live, Path.Combine("evals", "recordings", $"questions-{QuestionAgent.Version}", modelId));
        var answered = await Questions.RunAsync(answering, modelId, attacks: true);
        Console.WriteLine($"Model {modelId}, extraction prompt {ExtractionPrompt.Version}, question prompt {QuestionAgent.Version}");
        foreach (var agreement in read.Agreements)
        {
            var total = agreement.Kinds.Aggregate(new KindScore("all", 0, 0, 0, 0, 0, 0), KindScore.Add);
            Console.WriteLine($"\n{agreement.Agreement}: {total.Correct} of {total.Expected} rules found, {total.Proposed} proposed, {total.Flagged} flagged, {total.WrongAndUnflagged} wrong and unflagged. Payslips {agreement.Pay.Right} of {agreement.Pay.Cases} right, {agreement.Pay.NotChecked} not checked.");
            foreach (var kind in agreement.Kinds.Where(kind => kind.Proposed != kind.Expected || kind.Flagged > 0 || kind.WrongAndUnflagged > 0))
            {
                Console.WriteLine($"  {kind.Kind,-20} expected {kind.Expected}  proposed {kind.Proposed}  correct {kind.Correct}  flagged {kind.Flagged}  wrong-unflagged {kind.WrongAndUnflagged}");
            }

            foreach (var problem in agreement.Problems.Concat(agreement.Gaps.Select(gap => $"gap in {gap}")).Distinct())
            {
                Console.WriteLine($"  ! {problem}");
            }
        }

        Console.WriteLine($"\nQuestions on the planted agreement: {answered.Count(answer => answer.Verdict == "correct")} of {answered.Count} correct.");
        foreach (var answer in answered.Where(answer => answer.Verdict != "correct"))
        {
            Console.WriteLine($"  {answer.Id}: {answer.Verdict}: {answer.Text}\n    {string.Join("\n    ", answer.Trace.Select(step => step.Length > 200 ? step[..200] + "…" : step))}");
        }

        Console.WriteLine(record ? $"\nSpent ${spend.Dollars:0.0000} in {spend.Calls} calls." : "\nReplayed from recordings. Nothing spent.");
        if (args.Contains("--save"))
        {
            var file = Path.Combine("docs", "eval-report", "results", $"attacks-{ExtractionPrompt.Version}", $"{modelId}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { extraction = read, questions = answered }, EvalResults.Json) + "\n");
        }

        return 0;
    }
}

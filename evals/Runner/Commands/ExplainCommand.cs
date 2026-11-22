using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Overens.Agent;
using Overens.Agent.Questions;
using Overens.Gateway;
using Overens.Ingestion.Extraction;

namespace Overens.Evals.Commands;

/// <summary>How often a model's wording of a finding passes the guard: <c>--explain</c>.</summary>
internal static class ExplainCommand
{
    public static async Task<int> RunAsync(Run run)
    {
        var spend = run.Spend;
        var modelId = run.ModelId;
        var record = run.Record;
        var live = run.Live;
        // How often a model's wording of a finding passes the guard, on the findings in the hardest corpus agreement's pay data.
        using var wording = new ReplayChatClient(live, Path.Combine("evals", "recordings", $"explain-{FindingExplainer.Version}", modelId));
        var outcome = await Explanations.RunAsync(wording, modelId);
        Console.WriteLine($"Model {modelId}, explanation prompt {FindingExplainer.Version}");
        Console.WriteLine($"Findings: {outcome.Findings}. Model's wording shown: {outcome.Shown}. Replaced by the fixed wording: {outcome.Replaced.Count}.");
        foreach (var replaced in outcome.Replaced)
        {
            Console.WriteLine($"  replaced: {replaced}");
        }

        Console.WriteLine(record ? $"Spent ${spend.Dollars:0.0000} in {spend.Calls} calls." : "Replayed from recordings. Nothing spent.");
        return 0;
    }
}

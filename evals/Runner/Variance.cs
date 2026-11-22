using Microsoft.Extensions.AI;
using Overens.Gateway;
using Overens.Ingestion.Extraction;

namespace Overens.Evals;

/// <summary>One pass over every agreement: the totals of each set.</summary>
public sealed record VariancePass(int Pass, IReadOnlyDictionary<string, KindScore> Rules, IReadOnlyDictionary<string, PayScore> Pay);

/// <summary>How one agreement came out over the passes.</summary>
/// <param name="Found">True rules found, in each pass.</param>
/// <param name="Proposed">Rules proposed, in each pass.</param>
/// <param name="Flagged">Rules flagged or in conflict, in each pass.</param>
/// <param name="WrongAndUnflagged">Wrong rules the checks let through, in each pass.</param>
/// <param name="PayslipsRight">Payslips judged exactly right, in each pass.</param>
/// <param name="PayslipsNotChecked">Payslips with a line left unchecked, in each pass.</param>
/// <param name="SameEveryPass">Whether the scores, the problems and the gaps were the same in every pass.</param>
public sealed record AgreementSpread(
    string Agreement,
    string Set,
    int Expected,
    IReadOnlyList<int> Found,
    IReadOnlyList<int> Proposed,
    IReadOnlyList<int> Flagged,
    IReadOnlyList<int> WrongAndUnflagged,
    int Payslips,
    IReadOnlyList<int> PayslipsRight,
    IReadOnlyList<int> PayslipsNotChecked,
    bool SameEveryPass);

/// <summary>The same model asked the same thing several times.</summary>
/// <param name="Via">How the model was reached. These passes are not the API runs the other results come from.</param>
public sealed record VarianceResults(string Model, string PromptVersion, string Via, IReadOnlyList<VariancePass> Passes, IReadOnlyList<AgreementSpread> Agreements);

/// <summary>
/// How much a model's reading differs from one time to the next. Every other result here
/// is one answer per request. This asks for each several times, and each pass is recorded
/// in a directory of its own, so the passes replay like any other recording.
/// </summary>
public static class Variance
{
    /// <summary>The first pass is the ordinary recording of the evals. Each pass after it has a directory of its own.</summary>
    public static string Recordings(string modelId, int pass) => pass == 1
        ? Path.Combine("evals", "recordings", ExtractionPrompt.Version, modelId)
        : Path.Combine("evals", "recordings", $"variance-{ExtractionPrompt.Version}", modelId, $"pass-{pass}");

    public static async Task<VarianceResults> RunAsync(IChatClient? live, string modelId, int passes, string root = "", Func<Synthetic.AgreementSpec, bool>? only = null)
    {
        var runs = new List<EvalResults>();
        for (var pass = 1; pass <= passes; pass++)
        {
            using var model = new ReplayChatClient(live, Path.Combine(root, Recordings(modelId, pass)));
            runs.Add(await Evals.RunAsync(model, modelId, only));
        }

        var sets = runs[0].Agreements.Select(agreement => agreement.Set).Distinct().ToList();
        var spread = runs[0].Agreements.Select((first, at) =>
        {
            var each = runs.Select(run => run.Agreements[at]).ToList();
            KindScore Total(AgreementResult result) => result.Kinds.Aggregate(new KindScore("all", 0, 0, 0, 0, 0, 0), KindScore.Add);
            var written = each.Select(result => System.Text.Json.JsonSerializer.Serialize(result, EvalResults.Json)).Distinct().Count();
            return new AgreementSpread(
                first.Agreement,
                first.Set,
                Total(first).Expected,
                [.. each.Select(result => Total(result).Correct)],
                [.. each.Select(result => Total(result).Proposed)],
                [.. each.Select(result => Total(result).Flagged)],
                [.. each.Select(result => Total(result).WrongAndUnflagged)],
                first.Pay.Cases,
                [.. each.Select(result => result.Pay.Right)],
                [.. each.Select(result => result.Pay.NotChecked)],
                written == 1);
        }).ToList();

        return new(
            modelId,
            ExtractionPrompt.Version,
            "claude-code",
            [.. runs.Select((run, at) => new VariancePass(at + 1, sets.ToDictionary(set => set, run.Total), sets.ToDictionary(set => set, run.Pay)))],
            spread);
    }
}

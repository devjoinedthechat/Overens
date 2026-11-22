using Microsoft.Extensions.AI;
using Overens.Documents;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Reading;
using Overens.Rules;
using Overens.Synthetic;

namespace Overens.Evals;

/// <summary>
/// Agreements that the generator did not write. Every other test document comes from
/// <c>src/Synthetic</c>, whose phrasings the extraction prompt and the checks grew up beside.
/// These were written by hand, by writers shown the shape of a document and the meaning of
/// each kind of rule, and nothing of the generator, the prompt, the checks or the other
/// documents. Each writer also wrote down the rules its agreements state.
///
/// There are two sets. <c>independent</c> was written first, and the prompt was changed after
/// seeing how it was read. <c>unseen</c> was written while that change was made, and was read
/// for the first time to measure it.
///
/// They are kept as files in <c>data</c> and never regenerated. From there on they are
/// treated like the rest: printed as a PDF, read, extracted from, checked and scored.
/// </summary>
public static class Independent
{
    public static IReadOnlyList<string> Sets { get; } = ["independent", "unseen"];

    /// <summary>The agreements of a set, by folder name.</summary>
    public static IReadOnlyList<string> Ids(string set, string root = "") =>
        Directory.Exists(Path.Combine(root, "data", set))
            ? [.. Directory.GetDirectories(Path.Combine(root, "data", set)).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)]
            : [];

    public static string Recordings(string modelId) => Path.Combine("evals", "recordings", $"independent-{ExtractionPrompt.Version}", modelId);

    /// <summary>An agreement as written, and the rules its writer says it states.</summary>
    public static (AgreementDocument Document, RuleSet Truth) Load(string set, string id, string root = "")
    {
        var directory = Path.Combine(root, "data", set, id);
        var document = RuleJson.Read<AgreementDocument>(File.ReadAllText(Path.Combine(directory, "agreement.json")));
        var truth = RuleJson.Read<RuleSet>(File.ReadAllText(Path.Combine(directory, "rules.json")));
        return (document, truth);
    }

    /// <summary>Six months of pay under an agreement's true rules, from December of the year it starts.</summary>
    public static PayData Pay(string id, RuleSet truth)
    {
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(id);
        // A seed of the agreement's own, so that an agreement added later changes no other's pay data.
        var seed = 900 + id.Sum(letter => letter);
        return PayDataGenerator.Generate(truth, seed, new(truth.Rules.Min(rule => rule.InForce.From).Year, 12, 1), months: 6, employees: 12);
    }

    public static async Task<EvalResults> RunAsync(IChatClient model, string modelId, string root = "")
    {
        var extractor = new RuleExtractor(model, modelId);
        var results = new List<AgreementResult>();
        foreach (var set in Sets)
        {
            foreach (var id in Ids(set, root))
            {
                var (document, truth) = Load(set, id, root);
                var read = ClauseSegmenter.Segment(await new PdfLayoutReader().ReadAsync(AgreementPdf.Render(document)));
                var extraction = await extractor.ExtractAsync(read);
                results.Add(new(
                    id,
                    set,
                    ExtractionEval.Score(truth, extraction),
                    ExtractionEval.ScorePay(truth, extraction, Pay(id, truth)),
                    [.. extraction.Rules.SelectMany(rule => rule.Problems).Concat(extraction.Unreadable).Select(problem => $"{problem.RuleId}: {problem.Code}: {problem.Message}")],
                    [.. extraction.Gaps.Select(gap => $"{gap.RuleId}: {gap.Message}")]));
            }
        }

        return new(modelId, ExtractionPrompt.Version, results);
    }
}

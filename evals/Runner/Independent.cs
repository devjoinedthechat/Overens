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
/// These four were written by hand, by a writer who was shown the shape of a document and
/// the meaning of each kind of rule, and nothing of the generator, the prompt, the checks or
/// the other documents. The writer also wrote down the rules each one states.
///
/// They are kept as files in <c>data/independent</c> and never regenerated. From there on
/// they are treated like the rest: printed as a PDF, read, extracted from, checked and scored.
/// </summary>
public static class Independent
{
    public static IReadOnlyList<string> Ids { get; } = ["ind-a", "ind-b", "ind-c", "ind-d"];

    public static string Recordings(string modelId) => Path.Combine("evals", "recordings", $"independent-{ExtractionPrompt.Version}", modelId);

    /// <summary>An agreement as written, and the rules its writer says it states.</summary>
    public static (AgreementDocument Document, RuleSet Truth) Load(string id, string root = "")
    {
        var directory = Path.Combine(root, "data", "independent", id);
        var document = RuleJson.Read<AgreementDocument>(File.ReadAllText(Path.Combine(directory, "agreement.json")));
        var truth = RuleJson.Read<RuleSet>(File.ReadAllText(Path.Combine(directory, "rules.json")));
        return (document, truth);
    }

    /// <summary>Six months of pay under an agreement's true rules, from December of the year it starts.</summary>
    public static PayData Pay(string id, RuleSet truth)
    {
        ArgumentNullException.ThrowIfNull(truth);
        return PayDataGenerator.Generate(truth, 900 + Ids.TakeWhile(other => other != id).Count(), new(truth.Rules.Min(rule => rule.InForce.From).Year, 12, 1), months: 6, employees: 12);
    }

    public static async Task<EvalResults> RunAsync(IChatClient model, string modelId, string root = "")
    {
        var extractor = new RuleExtractor(model, modelId);
        var results = new List<AgreementResult>();
        foreach (var id in Ids)
        {
            var (document, truth) = Load(id, root);
            var read = ClauseSegmenter.Segment(await new PdfLayoutReader().ReadAsync(AgreementPdf.Render(document)));
            var extraction = await extractor.ExtractAsync(read);
            results.Add(new(
                id,
                "independent",
                ExtractionEval.Score(truth, extraction),
                ExtractionEval.ScorePay(truth, extraction, Pay(id, truth)),
                [.. extraction.Rules.SelectMany(rule => rule.Problems).Concat(extraction.Unreadable).Select(problem => $"{problem.RuleId}: {problem.Code}: {problem.Message}")],
                [.. extraction.Gaps.Select(gap => $"{gap.RuleId}: {gap.Message}")]));
        }

        return new(modelId, ExtractionPrompt.Version, results);
    }
}

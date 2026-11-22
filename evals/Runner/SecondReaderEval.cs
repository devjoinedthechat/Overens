using Microsoft.Extensions.AI;
using Overens.Gateway;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Reading;
using Overens.Rules;
using Overens.Synthetic;

namespace Overens.Evals;

/// <summary>One agreement read by two models, scored with and without the second reading.</summary>
/// <param name="Alone">The first reader's rules, as the checks left them.</param>
/// <param name="WithSecond">The same, with every rule the second reader differs on flagged.</param>
public sealed record SecondReaderAgreement(string Agreement, string Set, KindScore Alone, KindScore WithSecond, PayScore PayAlone, PayScore PayWithSecond);

/// <summary>A pairing of two recorded passes over the generator's agreements.</summary>
/// <param name="RightButFlagged">Correct rules of the first reader flagged only because the second differed.</param>
public sealed record SecondReaderPair(int FirstPass, int SecondPass, int Rules, int RightButFlagged);

public sealed record SecondReaderResults(
    string First,
    string Second,
    string PromptVersion,
    IReadOnlyList<SecondReaderAgreement> Agreements,
    IReadOnlyList<SecondReaderPair> Pairs)
{
    public KindScore Total(string set, bool withSecond) => Agreements.Where(agreement => agreement.Set == set)
        .Select(agreement => withSecond ? agreement.WithSecond : agreement.Alone)
        .Aggregate(new KindScore("all", 0, 0, 0, 0, 0, 0), KindScore.Add);
}

/// <summary>
/// What a second reader would add: for every agreement, the first model's rules are held
/// against the second model's reading of the same agreement, and both are scored. Pairs of
/// repeat passes show how often two correct readings still differ.
/// </summary>
public static class SecondReaderEval
{
    public static async Task<SecondReaderResults> RunAsync(string first, string second, string root = "", int firstPasses = 0, int secondPasses = 0)
    {
        var agreements = new List<SecondReaderAgreement>();
        var documents = Evals.Agreements.Select(agreement => (agreement.Set, agreement.Spec.Id, Load: (Func<(Documents.AgreementDocument, RuleSet, PayData)>)(() =>
        {
            var generated = Corpus.Generate(agreement.Spec);
            return (generated.Document, generated.Truth, Corpus.Pay(agreement.Spec, generated));
        }), Recordings: (Func<string, string>)(model => Path.Combine(root, "evals", "recordings", ExtractionPrompt.Version, model))))
            .Concat(Independent.Sets.SelectMany(set => Independent.Ids(set, root).Select(id => (set, id, Load: (Func<(Documents.AgreementDocument, RuleSet, PayData)>)(() =>
            {
                var (document, truth) = Independent.Load(set, id, root);
                return (document, truth, Independent.Pay(id, truth));
            }), Recordings: (Func<string, string>)(model => Path.Combine(root, Independent.Recordings(model)))))))
            .ToList();

        foreach (var (set, id, load, recordings) in documents)
        {
            var (document, truth, pay) = load();
            var read = ClauseSegmenter.Segment(await new PdfLayoutReader().ReadAsync(AgreementPdf.Render(document)));
            var alone = await Extract(recordings(first), first, read);
            var other = await Extract(recordings(second), second, read);
            var together = SecondReading.Apply(alone, other);
            agreements.Add(new(
                id,
                set,
                Sum(ExtractionEval.Score(truth, alone)),
                Sum(ExtractionEval.Score(truth, together)),
                ExtractionEval.ScorePay(truth, alone, pay),
                ExtractionEval.ScorePay(truth, together, pay)));
        }

        // Two correct readings can still differ, so every recorded pass of one is paired with every pass of the other.
        var pairs = new List<SecondReaderPair>();
        // Each agreement is printed and read once, not once for every pairing.
        var generatedAgreements = new List<(SyntheticAgreement Generated, ReadAgreement Read)>();
        if (firstPasses > 0 && secondPasses > 0)
        {
            foreach (var (_, spec) in Evals.Agreements)
            {
                var generated = Corpus.Generate(spec);
                generatedAgreements.Add((generated, ClauseSegmenter.Segment(await new PdfLayoutReader().ReadAsync(AgreementPdf.Render(generated.Document)))));
            }
        }

        for (var firstPass = 1; firstPass <= firstPasses; firstPass++)
        {
            for (var secondPass = 1; secondPass <= secondPasses; secondPass++)
            {
                var (rules, needless) = (0, 0);
                foreach (var (generated, read) in generatedAgreements)
                {
                    var alone = await Extract(Path.Combine(root, Variance.Recordings(first, firstPass)), first, read);
                    var together = SecondReading.Apply(alone, await Extract(Path.Combine(root, Variance.Recordings(second, secondPass)), second, read));
                    var (before, after) = (Sum(ExtractionEval.Score(generated.Truth, alone)), Sum(ExtractionEval.Score(generated.Truth, together)));
                    rules += after.Proposed;
                    needless += after.RightButFlagged - before.RightButFlagged;
                }

                pairs.Add(new(firstPass, secondPass, rules, needless));
            }
        }

        return new(first, second, ExtractionPrompt.Version, agreements, pairs);
    }

    private static async Task<ExtractionResult> Extract(string recordings, string model, ReadAgreement read)
    {
        using IChatClient recorded = new ReplayChatClient(null, recordings);
        return await new RuleExtractor(recorded, model).ExtractAsync(read);
    }

    private static KindScore Sum(IEnumerable<KindScore> kinds) => kinds.Aggregate(new KindScore("all", 0, 0, 0, 0, 0, 0), KindScore.Add);
}

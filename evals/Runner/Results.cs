using System.Text.Json;
using Microsoft.Extensions.AI;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Reading;
using Overens.Rules;
using Overens.Synthetic;

namespace Overens.Evals;

/// <summary>One agreement's result.</summary>
public sealed record AgreementResult(string Agreement, string Set, IReadOnlyList<KindScore> Kinds, PayScore Pay, IReadOnlyList<string> Problems, IReadOnlyList<string> Gaps);

/// <summary>Every agreement's result for one model and one version of the prompt.</summary>
public sealed record EvalResults(string Model, string PromptVersion, IReadOnlyList<AgreementResult> Agreements)
{
    public static JsonSerializerOptions Json { get; } = new(RuleJson.Options);

    public KindScore Total(string set) => Agreements
        .Where(agreement => agreement.Set == set)
        .SelectMany(agreement => agreement.Kinds)
        .Aggregate(new KindScore("all", 0, 0, 0, 0, 0, 0), KindScore.Add);

    public PayScore Pay(string set)
    {
        var pay = Agreements.Where(agreement => agreement.Set == set).Select(agreement => agreement.Pay).ToList();
        return new(pay.Sum(score => score.Cases), pay.Sum(score => score.Right), pay.Sum(score => score.MissedErrors), pay.Sum(score => score.FalseAlarms));
    }
}

public static class Evals
{
    /// <summary>The corpus the prompt was written with, and the set added after it was fixed.</summary>
    public static IReadOnlyList<(string Set, AgreementSpec Spec)> Agreements { get; } =
    [
        .. Corpus.Agreements.Select(spec => ("corpus", spec)),
        .. Corpus.Challenge.Select(spec => ("challenge", spec)),
    ];

    /// <summary>The agreements that work against the reader. Scored like the others, reported apart.</summary>
    public static IReadOnlyList<(string Set, AgreementSpec Spec)> Attacks { get; } = [.. Corpus.Attacks.Select(spec => ("attack", spec))];

    /// <summary>Runs extraction on every agreement, from its PDF, and scores it.</summary>
    public static async Task<EvalResults> RunAsync(IChatClient model, string modelId, Func<AgreementSpec, bool>? only = null, bool attacks = false)
    {
        var extractor = new RuleExtractor(model, modelId);
        var results = new List<AgreementResult>();
        foreach (var (set, spec) in (attacks ? Attacks : Agreements).Where(agreement => only?.Invoke(agreement.Spec) ?? true))
        {
            var agreement = Corpus.Generate(spec);
            var read = ClauseSegmenter.Segment(await new PdfLayoutReader().ReadAsync(AgreementPdf.Render(agreement.Document)));
            var extraction = await extractor.ExtractAsync(read);
            results.Add(new(
                spec.Id,
                set,
                ExtractionEval.Score(agreement.Truth, extraction),
                ExtractionEval.ScorePay(extraction, Corpus.Pay(spec, agreement)),
                [.. extraction.Rules.SelectMany(rule => rule.Problems).Concat(extraction.Unreadable).Select(problem => $"{problem.RuleId}: {problem.Code}: {problem.Message}")],
                [.. extraction.Gaps.Select(gap => $"{gap.RuleId}: {gap.Message}")]));
        }

        return new(modelId, ExtractionPrompt.Version, results);
    }
}

using System.Globalization;
using Microsoft.Extensions.AI;
using Overens.Evals;
using Overens.Gateway;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Reading;
using Overens.Rules;
using Overens.Synthetic;

// Extraction on the corpus:
//
//   dotnet run --project evals/Runner -- [--model claude-sonnet-5-5] [--only hard-2026] [--cap 2] [--record]
//
// Without --record only recorded answers are used and nothing is spent. With it,
// ANTHROPIC_API_KEY must be set; calls not yet recorded go to the model, up to the cap in dollars.
string Option(string name, string fallback) => Array.IndexOf(args, name) is >= 0 and var at && at + 1 < args.Length ? args[at + 1] : fallback;
var modelId = Option("--model", Models.Sonnet);
var only = Option("--only", "");
var cap = decimal.Parse(Option("--cap", "2"), CultureInfo.InvariantCulture);
var record = args.Contains("--record");
var recordings = Path.Combine("evals", "recordings", ExtractionPrompt.Version, modelId);

var spend = new Spend();
IChatClient? live = null;
if (record)
{
    var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.Error.WriteLine("--record needs ANTHROPIC_API_KEY.");
        return 2;
    }

    live = new MeteredChatClient(Claude.Create(key), spend, cap);
}

using var model = new ReplayChatClient(live, recordings);
var extractor = new RuleExtractor(model, modelId);
var totals = new Dictionary<string, KindScore>();
foreach (var spec in Corpus.Agreements.Where(spec => only.Length == 0 || spec.Id == only))
{
    var agreement = Corpus.Generate(spec);
    var read = ClauseSegmenter.Segment(await new PdfLayoutReader().ReadAsync(AgreementPdf.Render(agreement.Document)));
    var extraction = await extractor.ExtractAsync(read);

    Console.WriteLine($"\n{spec.Id}");
    foreach (var score in ExtractionEval.Score(agreement.Truth, extraction))
    {
        Console.WriteLine($"  {score.Kind,-20} expected {score.Expected}  proposed {score.Proposed}  correct {score.Correct}  flagged {score.Flagged}  wrong-unflagged {score.WrongAndUnflagged}  right-flagged {score.RightButFlagged}");
        var sum = totals.GetValueOrDefault(score.Kind) ?? new(score.Kind, 0, 0, 0, 0, 0, 0);
        totals[score.Kind] = sum with
        {
            Expected = sum.Expected + score.Expected,
            Proposed = sum.Proposed + score.Proposed,
            Correct = sum.Correct + score.Correct,
            Flagged = sum.Flagged + score.Flagged,
            WrongAndUnflagged = sum.WrongAndUnflagged + score.WrongAndUnflagged,
            RightButFlagged = sum.RightButFlagged + score.RightButFlagged,
        };
    }

    foreach (var problem in extraction.Rules.SelectMany(rule => rule.Problems).Concat(extraction.Unreadable))
    {
        Console.WriteLine($"  ! {problem.RuleId}: {problem.Code}: {problem.Message}");
    }

    if (args.Contains("--show"))
    {
        Console.WriteLine(RuleJson.Write(extraction.Rules.Select(rule => rule.Rule)));
    }
}

var all = totals.Values.ToList();
Console.WriteLine($"\nModel {modelId}, prompt {ExtractionPrompt.Version}");
Console.WriteLine($"Rules: {all.Sum(score => score.Correct)} of {all.Sum(score => score.Expected)} found, {all.Sum(score => score.Proposed)} proposed, {all.Sum(score => score.WrongAndUnflagged)} wrong and unflagged, {all.Sum(score => score.RightButFlagged)} right but flagged.");
Console.WriteLine(record ? $"Spent ${spend.Dollars:0.0000} in {spend.Calls} calls ({spend.InputTokens} tokens in, {spend.OutputTokens} out)." : "Replayed from recordings. Nothing spent.");
return 0;

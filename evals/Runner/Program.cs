using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Overens.Agent;
using Overens.Agent.Questions;
using Overens.Evals;
using Overens.Gateway;
using Overens.Ingestion.Extraction;

// Extraction on every agreement, scored against the rules each one really states:
//
//   dotnet run --project evals/Runner -- [--model claude-sonnet-5-5] [--only twist-all] [--cap 2] [--record] [--save]
//
// Without --record only recorded answers are used and nothing is spent. With it,
// ANTHROPIC_API_KEY must be set; calls not yet recorded go to the model, up to the cap in dollars.
// --save writes the results to docs/eval-report/results, where a test holds them to the recordings.
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

if (args.Contains("--explain"))
{
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

if (args.Contains("--attacks"))
{
    // Two hostile agreements: one with instructions planted in it, one that contradicts itself.
    using var reading = new ReplayChatClient(live, recordings);
    var read = await Evals.RunAsync(reading, modelId, attacks: true);
    using var answering = new ReplayChatClient(live, Path.Combine("evals", "recordings", $"questions-{QuestionAgent.Version}", modelId));
    var answered = await Questions.RunAsync(answering, modelId, attacks: true);
    Console.WriteLine($"Model {modelId}, extraction prompt {ExtractionPrompt.Version}, question prompt {QuestionAgent.Version}");
    foreach (var agreement in read.Agreements)
    {
        var total = agreement.Kinds.Aggregate(new KindScore("all", 0, 0, 0, 0, 0, 0), KindScore.Add);
        Console.WriteLine($"\n{agreement.Agreement}: {total.Correct} of {total.Expected} rules found, {total.Proposed} proposed, {total.Flagged} flagged, {total.WrongAndUnflagged} wrong and unflagged. Payslips {agreement.Pay.Right} of {agreement.Pay.Cases} right.");
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
        var file = Path.Combine("docs", "eval-report", "results", "attacks", $"{modelId}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { extraction = read, questions = answered }, EvalResults.Json) + "\n");
    }

    return 0;
}

if (args.Contains("--questions"))
{
    // Questions with known answers, asked of the question agent with the true rules and the agreement's clauses.
    using var asked = new ReplayChatClient(live, Path.Combine("evals", "recordings", $"questions-{QuestionAgent.Version}", modelId));
    var answers = await Questions.RunAsync(asked, modelId, only);
    foreach (var answer in answers.Where(answer => answer.Verdict != "correct" || args.Contains("--show")))
    {
        Console.WriteLine($"\n{answer.Agreement} {answer.Id} ({answer.Kind}): {answer.Verdict}\n  {answer.Text}");
        foreach (var step in answer.Trace)
        {
            Console.WriteLine($"    {(step.Length > 220 ? step[..220] + "…" : step)}");
        }
    }

    Console.WriteLine($"\nModel {modelId}, question prompt {QuestionAgent.Version}");
    foreach (var kind in answers.GroupBy(answer => answer.Kind))
    {
        Console.WriteLine($"{kind.Key,-13} {kind.Count(answer => answer.Verdict == "correct")} of {kind.Count()} correct  ({string.Join(", ", kind.Where(answer => answer.Verdict != "correct").GroupBy(answer => answer.Verdict).Select(verdict => $"{verdict.Count()} {verdict.Key}"))})");
    }

    Console.WriteLine(record ? $"Spent ${spend.Dollars:0.0000} in {spend.Calls} calls ({spend.InputTokens} tokens in, {spend.OutputTokens} out)." : "Replayed from recordings. Nothing spent.");
    if (args.Contains("--save") && only.Length == 0)
    {
        var file = Path.Combine("docs", "eval-report", "results", $"questions-{QuestionAgent.Version}", $"{modelId}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(answers, EvalResults.Json) + "\n");
    }

    return 0;
}

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

    Console.WriteLine($"  payslips: {agreement.Pay.Right} of {agreement.Pay.Cases} right, {agreement.Pay.MissedErrors} with a missed error, {agreement.Pay.FalseAlarms} false alarms");
}

Console.WriteLine($"\nModel {modelId}, prompt {ExtractionPrompt.Version}");
foreach (var set in results.Agreements.Select(agreement => agreement.Set).Distinct())
{
    var (rules, pay) = (results.Total(set), results.Pay(set));
    Console.WriteLine($"{set,-10} rules: {rules.Correct} of {rules.Expected} found, {rules.Proposed} proposed, {rules.Flagged} flagged, {rules.WrongAndUnflagged} wrong and unflagged, {rules.RightButFlagged} right but flagged.  payslips: {pay.Right} of {pay.Cases} right.");
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

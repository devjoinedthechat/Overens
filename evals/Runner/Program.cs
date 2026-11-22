using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Overens.Agent;
using Overens.Agent.Questions;
using Overens.Evals;
using Overens.Evals.Commands;
using Overens.Gateway;
using Overens.Ingestion.Extraction;

// The evals. Each replays recorded answers and spends nothing, unless --record is given.
//
//   dotnet run --project evals/Runner -- [command] [--model claude-sonnet-5-5] [--only twist-all] [--record [--via claude-code] [--cap 2]] [--save]
//
//   (none)           extraction on the sixteen generated agreements          Commands/ExtractCommand.cs
//   --independent    the eight agreements written by hand                     Commands/IndependentCommand.cs
//   --variance N     the same extraction asked N times                        Commands/VarianceCommand.cs
//   --second-reader  each rule held against a second model's reading          Commands/SecondReaderCommand.cs
//   --planted        mistakes planted in correct answers                      Commands/PlantedCommand.cs
//   --attacks        the hostile agreements                                   Commands/AttacksCommand.cs
//   --questions      48 questions with known answers                          Commands/QuestionsCommand.cs
//   --explain        how often a model's wording of a finding passes the guard Commands/ExplainCommand.cs
//
// With --record, answers not yet recorded go to the model: through the API with ANTHROPIC_API_KEY set,
// up to the cap in dollars, or with --via claude-code through Claude Code's print mode, with no key.
// --save writes the results to docs/eval-report/results, where a test holds them to the recordings.
string Option(string name, string fallback) => Array.IndexOf(args, name) is >= 0 and var at && at + 1 < args.Length ? args[at + 1] : fallback;
var modelId = Option("--model", Models.Sonnet);
var only = Option("--only", "");
var cap = decimal.Parse(Option("--cap", "2"), CultureInfo.InvariantCulture);
var record = args.Contains("--record");
var recordings = Path.Combine("evals", "recordings", ExtractionPrompt.Version, modelId);

var spend = new Spend();
IChatClient? live = null;
var viaClaudeCode = Option("--via", "api") == "claude-code";
if (record && viaClaudeCode)
{
    // Through Claude Code's print mode, with no key. Nothing is priced, so there is no cap to hold.
    live = new ClaudeCodeChatClient();
}
else if (record)
{
    var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.Error.WriteLine("--record needs ANTHROPIC_API_KEY.");
        return 2;
    }

    // No failover while recording: an answer is filed under the model asked, so that model must be the one that gave it.
    live = new MeteredChatClient(Claude.Create(key, Claude.Background, failover: false), spend, cap);
}

var run = new Run(args, modelId, only, record, live, spend, recordings);
return true switch
{
    _ when args.Contains("--variance") => await VarianceCommand.RunAsync(run),
    _ when args.Contains("--explain") => await ExplainCommand.RunAsync(run),
    _ when args.Contains("--second-reader") => await SecondReaderCommand.RunAsync(run),
    _ when args.Contains("--independent") => await IndependentCommand.RunAsync(run),
    _ when args.Contains("--planted") => await PlantedCommand.RunAsync(run),
    _ when args.Contains("--attacks") => await AttacksCommand.RunAsync(run),
    _ when args.Contains("--questions") => await QuestionsCommand.RunAsync(run),
    _ => await ExtractCommand.RunAsync(run),
};

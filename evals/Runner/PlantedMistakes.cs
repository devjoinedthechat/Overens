using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Overens.Gateway;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Reading;
using Overens.Rules;
using Overens.Synthetic;

namespace Overens.Evals;

/// <summary>
/// One way a model can get a rule wrong, as a change to the answer it gave.
/// </summary>
/// <param name="Name">A short name, used in the results.</param>
/// <param name="Group">What kind of mistake it is. See <see cref="PlantedMistakes.Groups"/>.</param>
/// <param name="Kind">The kind of rule it applies to, or null for every kind.</param>
/// <param name="RuleStaysRight">
/// True when the rule still says what the agreement says and only its evidence is spoiled:
/// a reworded quote, or a sum the model made itself that happens to be right. Such a rule
/// must be flagged all the same.
/// </param>
/// <param name="Plant">
/// Makes the mistake in the rule at a place in the answer's list of rules. Returns false,
/// and changes nothing, when the mistake cannot be made in that rule.
/// </param>
public sealed record Mistake(string Name, string Group, string? Kind, bool RuleStaysRight, Func<JsonArray, int, bool> Plant);

/// <summary>What became of one planted mistake.</summary>
/// <param name="Agreement">The agreement whose answer was changed.</param>
/// <param name="Kind">The kind of rule.</param>
/// <param name="From">The day the changed rule starts, as the model gave it.</param>
/// <param name="Mistake">The mistake's name.</param>
/// <param name="Outcome">
/// <c>stopped</c>: the rule is flagged or in conflict, and cannot be published until a person acts.
/// <c>reported</c>: the rule looks sound, and the run tells the reviewer that days or a kind of rule are missing.
/// <c>silent</c>: nothing tells the reviewer anything.
/// <c>no_change</c>: the changed answer still gives the true rules, so there was no mistake to catch.
/// </param>
/// <param name="PayslipsWrong">For a mistake that was not stopped: payslips the rules then judge wrongly.</param>
/// <param name="PayslipsNotChecked">For a mistake that was not stopped: payslips with a line the rules then leave unchecked.</param>
public sealed record PlantedOutcome(string Agreement, string Kind, string From, string Mistake, string Outcome, int PayslipsWrong, int PayslipsNotChecked);

/// <summary>The outcomes of one mistake over every rule it could be planted in.</summary>
public sealed record MistakeTotal(string Mistake, string Group, int Planted, int Stopped, int Reported, int Silent, int NoChange);

/// <summary>Every planted mistake's outcome for one model's recorded answers.</summary>
/// <param name="Totals">One line per mistake.</param>
/// <param name="NotStopped">Each mistake that was not stopped, with what it did to the payslips.</param>
public sealed record PlantedResults(string Model, string PromptVersion, IReadOnlyList<MistakeTotal> Totals, IReadOnlyList<PlantedOutcome> NotStopped)
{
    /// <summary>The totals of one group of mistakes, or of all of them.</summary>
    public MistakeTotal Total(string? group = null) => Totals
        .Where(total => group is null || total.Group == group)
        .Aggregate(new MistakeTotal("all", group ?? "all", 0, 0, 0, 0, 0), (sum, total) => sum with
        {
            Planted = sum.Planted + total.Planted,
            Stopped = sum.Stopped + total.Stopped,
            Reported = sum.Reported + total.Reported,
            Silent = sum.Silent + total.Silent,
            NoChange = sum.NoChange + total.NoChange,
        });
}

/// <summary>
/// Measures the model-free checks on answers that are wrong. The models made almost no
/// mistakes on the test documents, so a perfect score says little about the checks. Here
/// each correct recorded answer is spoiled one way at a time, in the ways a model gets a
/// rule wrong, and the whole of extraction runs on it as if the model had answered so.
/// </summary>
public static partial class PlantedMistakes
{
    /// <summary>
    /// A figure the agreement prints nowhere: made up, or worked out by the model. The amounts
    /// are odd ones, 37 øre and 0,37 points, so that none can land on a figure that is printed.
    /// </summary>
    public const string NotPrinted = "figure_not_printed";

    /// <summary>
    /// A figure one step from the right one: a krone, half a point, an hour, a year. This is
    /// how a figure is misread, and the neighbour is sometimes printed nearby as something else.
    /// </summary>
    public const string NearMiss = "figure_one_step_off";

    /// <summary>The agreement's own figure, put where it does not belong: another band, another year, the other party.</summary>
    public const string Misplaced = "figure_misplaced";

    /// <summary>A part of the rule, or the whole rule, left out.</summary>
    public const string LeftOut = "left_out";

    /// <summary>The rule given for the wrong days.</summary>
    public const string WrongDays = "wrong_days";

    /// <summary>The rule's evidence spoiled: its quote or its clause.</summary>
    public const string Evidence = "evidence";

    public static IReadOnlyList<string> Groups { get; } = [NotPrinted, NearMiss, Misplaced, LeftOut, WrongDays, Evidence];


    /// <summary>Plants every mistake in every rule it fits, in every agreement, and says what became of each.</summary>
    public static async Task<PlantedResults> RunAsync(IChatClient recorded, string modelId, Func<AgreementSpec, bool>? only = null) =>
        (await RunWithSilentAsync(recorded, modelId, only)).Results;

    /// <summary>
    /// The study, and with it the wrong rules that pass every check in silence, as a reviewer
    /// would see them: each with the agreement it was read from and the mistake that was planted.
    /// </summary>
    public static async Task<(PlantedResults Results, IReadOnlyList<(AgreementSpec Spec, ReadAgreement Read, ProposedRule Rule, string Mistake)> Silent)> RunWithSilentAsync(
        IChatClient recorded, string modelId, Func<AgreementSpec, bool>? only = null)
    {
        // The agreements do not depend on each other, so they are worked through at the same time. The order of the result is theirs.
        var perAgreement = await Task.WhenAll(Evals.Agreements
            .Where(agreement => only?.Invoke(agreement.Spec) ?? true)
            .Select(agreement => Task.Run(async () =>
            {
                var silent = new List<(AgreementSpec, ReadAgreement, ProposedRule, string)>();
                var expected = Corpus.Generate(agreement.Spec).Truth.Rules.Select(ExtractionEval.Meaning).ToHashSet();
                var outcomes = await RunAsync(recorded, modelId, agreement.Spec, (outcome, read, extraction) =>
                {
                    if (outcome.Outcome != "silent")
                    {
                        return;
                    }

                    // The wrong rule is the one of the planted kind that no true rule matches.
                    foreach (var wrong in extraction.Rules.Where(rule => ExtractionEval.KindOf(rule.Rule) == outcome.Kind && rule.Problems.Count == 0 && !expected.Contains(ExtractionEval.Meaning(rule.Rule))))
                    {
                        silent.Add((agreement.Spec, read, wrong, outcome.Mistake));
                    }
                });
                return (Outcomes: outcomes, Silent: silent);
            })));
        var outcomes = perAgreement.SelectMany(found => found.Outcomes).ToList();
        var totals = All
            .GroupBy(mistake => (mistake.Name, mistake.Group))
            .Select(mistake =>
            {
                var planted = outcomes.Where(outcome => outcome.Mistake == mistake.Key.Name).ToList();
                return new MistakeTotal(
                    mistake.Key.Name,
                    mistake.Key.Group,
                    planted.Count,
                    planted.Count(outcome => outcome.Outcome == "stopped"),
                    planted.Count(outcome => outcome.Outcome == "reported"),
                    planted.Count(outcome => outcome.Outcome == "silent"),
                    planted.Count(outcome => outcome.Outcome == "no_change"));
            })
            .ToList();
        return (new(modelId, ExtractionPrompt.Version, totals, [.. outcomes.Where(outcome => outcome.Outcome is "reported" or "silent")]), [.. perAgreement.SelectMany(found => found.Silent)]);
    }

    private static async Task<List<PlantedOutcome>> RunAsync(IChatClient recorded, string modelId, AgreementSpec spec, Action<PlantedOutcome, ReadAgreement, ExtractionResult>? keep = null)
    {
        var outcomes = new List<PlantedOutcome>();
        var agreement = Corpus.Generate(spec);
        var read = await ReadOnce.Agreement(agreement.Document);
        var pay = Corpus.Pay(spec, agreement);
        using var answers = new RewritingChatClient(recorded);
        var extractor = new RuleExtractor(answers, modelId);

        var untouched = await extractor.ExtractAsync(read);
        var before = ExtractionEval.Score(agreement.Truth, untouched);
        foreach (var kind in ExtractionPrompt.Kinds.Select(kind => kind.Name))
        {
            // Only rules that were right and unflagged to begin with: a mistake is planted in a sound answer.
            var sound = before.Single(score => score.Kind == kind);
            if (sound.Correct != sound.Expected || sound.Proposed != sound.Expected || sound.Flagged > 0 || untouched.Unreadable.Any(problem => problem.RuleId == kind))
            {
                continue;
            }

            var given = JsonNode.Parse(answers.Given[kind])!;
            var count = given["rules"]!.AsArray().Count;
            foreach (var mistake in All.Where(mistake => mistake.Kind is null || mistake.Kind == kind))
            {
                for (var at = 0; at < count; at++)
                {
                    var spoiled = given.DeepClone();
                    var from = spoiled["rules"]![at]!["from"]!.GetValue<string>();
                    if (!mistake.Plant(spoiled["rules"]!.AsArray(), at))
                    {
                        continue;
                    }

                    answers.Instead = (kind, spoiled.ToJsonString());
                    var extraction = await extractor.ExtractAsync(read);
                    answers.Instead = null;
                    outcomes.Add(Judge(spec.Id, kind, from, mistake, agreement.Truth, untouched, extraction, pay));
                    keep?.Invoke(outcomes[^1], read, extraction);
                }
            }
        }

        return outcomes;
    }

    /// <summary>What the checks made of one spoiled answer, against what they made of the answer as the model gave it.</summary>
    public static PlantedOutcome Judge(string agreement, string kind, string from, Mistake mistake, RuleSet truth, ExtractionResult untouched, ExtractionResult extraction, PayData pay)
    {
        ArgumentNullException.ThrowIfNull(mistake);
        ArgumentNullException.ThrowIfNull(truth);
        ArgumentNullException.ThrowIfNull(untouched);
        ArgumentNullException.ThrowIfNull(extraction);
        var before = ExtractionEval.Score(truth, untouched).Single(score => score.Kind == kind);
        var after = ExtractionEval.Score(truth, extraction).Single(score => score.Kind == kind);
        var wrong = after.Proposed - ProposedAndRight(truth, extraction, kind);
        var known = untouched.Gaps.Select(gap => (gap.RuleId, gap.Code, gap.Message)).ToHashSet();
        var told = extraction.Gaps.Any(gap => !known.Contains((gap.RuleId, gap.Code, gap.Message)))
            || extraction.Unreadable.Any(problem => problem.RuleId == kind);

        string outcome;
        if (wrong > 0)
        {
            // Every wrong rule flagged or in conflict: none can be published as it stands.
            outcome = after.WrongAndUnflagged == 0 ? "stopped" : told ? "reported" : "silent";
        }
        else if (after.Correct < before.Correct)
        {
            // No wrong rule, and a true one is gone.
            outcome = told ? "reported" : "silent";
        }
        else if (mistake.RuleStaysRight)
        {
            outcome = after.Flagged > before.Flagged ? "stopped" : "silent";
        }
        else
        {
            outcome = "no_change";
        }

        if (outcome is not ("reported" or "silent"))
        {
            return new(agreement, kind, from, mistake.Name, outcome, 0, 0);
        }

        var payslips = ExtractionEval.ScorePay(truth, extraction, pay);
        return new(agreement, kind, from, mistake.Name, outcome, payslips.MissedErrors + payslips.FalseAlarms, payslips.NotChecked);
    }

    private static int ProposedAndRight(RuleSet truth, ExtractionResult extraction, string kind)
    {
        var expected = truth.Rules.Where(rule => ExtractionEval.KindOf(rule) == kind).Select(ExtractionEval.Meaning).ToHashSet();
        return extraction.Rules.Count(rule => ExtractionEval.KindOf(rule.Rule) == kind && expected.Contains(ExtractionEval.Meaning(rule.Rule)));
    }

    /// <summary>
    /// Hands back a model's recorded answers, keeps them by kind of rule, and gives one
    /// changed answer in place of the recorded one when told to.
    /// </summary>
    private sealed class RewritingChatClient(IChatClient recorded) : IChatClient
    {
        /// <summary>The answer the model gave for each kind of rule.</summary>
        public Dictionary<string, string> Given { get; } = [];

        /// <summary>A kind of rule, and the answer to give for it in place of the model's.</summary>
        public (string Kind, string Answer)? Instead { get; set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var kind = (options?.ResponseFormat as ChatResponseFormatJson)?.SchemaName ?? throw new InvalidOperationException("Only extraction requests are answered here.");
            string? given;
            lock (Given)
            {
                Given.TryGetValue(kind, out given);
            }

            if (given is null)
            {
                given = (await recorded.GetResponseAsync(messages, options, cancellationToken)).RequireComplete().Text;
                lock (Given)
                {
                    Given[kind] = given;
                }
            }

            return new(new ChatMessage(ChatRole.Assistant, Instead is { } instead && instead.Kind == kind ? instead.Answer : given));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        // The recorded answers belong to whoever made the client that holds them.
        public void Dispose()
        {
        }
    }
}

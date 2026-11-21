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
public static class PlantedMistakes
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

    public static IReadOnlyList<Mistake> All { get; } =
    [
        // A figure the agreement prints nowhere.
        new("rate_invented", NotPrinted, "minimum_wage", false, (rules, at) =>
            Items(rules[at], "bands") is { Count: > 0 } bands && Add(bands[^1], "hourly_rate", 0.37m)),
        new("increase_invented", NotPrinted, "minimum_wage", false, (rules, at) => Add(rules[at], "increase", 0.37m)),
        new("sum_made_by_the_model", NotPrinted, "minimum_wage", true, (rules, at) =>
        {
            // The model reports the rate after the increase, having added it up itself.
            if (Number(rules[at], "increase") is not { } increase || at == 0 || Items(rules[at - 1], "bands") is not { Count: > 0 } before)
            {
                return false;
            }

            var summed = new JsonArray();
            foreach (var band in before)
            {
                summed.Add(new JsonObject { ["from_months"] = band!["from_months"]!.GetValue<int>(), ["hourly_rate"] = Number(band, "hourly_rate")!.Value + increase });
            }

            rules[at]!["increase"] = null;
            rules[at]!["bands"] = summed;
            return true;
        }),
        new("seniority_invented", NotPrinted, "minimum_wage", false, (rules, at) =>
            Items(rules[at], "bands") is { Count: > 1 } bands && Add(bands[^1], "from_months", 7)),
        new("daily_norm_invented", NotPrinted, "overtime", false, (rules, at) => Add(rules[at], "daily_norm_minutes", 7)),
        new("overtime_percent_invented", NotPrinted, "overtime", false, (rules, at) => Add(Items(rules[at], "tiers")![0], "percent", 0.37m)),
        new("tier_length_invented", NotPrinted, "overtime", false, (rules, at) => Add(Items(rules[at], "tiers")![0], "minutes", 37)),
        new("supplement_invented", NotPrinted, "inconvenient_hours", false, (rules, at) => Add(Items(rules[at], "windows")![0], "amount_per_hour", 0.37m)),
        new("window_time_invented", NotPrinted, "inconvenient_hours", false, (rules, at) => Later(Items(rules[at], "windows")![0]!, minutes: 17)),
        new("pension_percent_invented", NotPrinted, "pension", false, (rules, at) => Add(rules[at], "employer_percent", 0.37m)),
        new("qualifying_period_invented", NotPrinted, "pension", false, (rules, at) =>
            Number(rules[at], "qualifying_months") > 0 && Add(rules[at], "qualifying_months", 7)),
        new("share_worked_out_by_the_model", NotPrinted, "pension", true, (rules, at) =>
        {
            // The model reports the percentage it made of "half", not the share as printed.
            if (rules[at]!["employee_share"]?.GetValue<string>() is not { } share)
            {
                return false;
            }

            rules[at]!["employee_percent"] = Shares.Of(share, Number(rules[at], "employer_percent")!.Value);
            rules[at]!["employee_share"] = null;
            return true;
        }),
        new("another_share", NotPrinted, "pension", false, (rules, at) =>
        {
            if (rules[at]!["employee_share"]?.GetValue<string>() is not { } share)
            {
                return false;
            }

            rules[at]!["employee_share"] = share == "half" ? "third" : "half";
            return true;
        }),
        new("percent_invented", NotPrinted, "holiday_allowance", false, (rules, at) => Add(rules[at], "percent", 0.37m)),
        new("percent_invented", NotPrinted, "special_savings", false, (rules, at) => Add(rules[at], "percent", 0.37m)),

        // A figure one step from the right one.
        new("rate_a_krone_off", NearMiss, "minimum_wage", false, (rules, at) =>
            Items(rules[at], "bands") is { Count: > 0 } bands && Add(bands[^1], "hourly_rate", 1)),
        new("increase_a_krone_off", NearMiss, "minimum_wage", false, (rules, at) => Add(rules[at], "increase", 1)),
        new("seniority_a_year_off", NearMiss, "minimum_wage", false, (rules, at) =>
            Items(rules[at], "bands") is { Count: > 1 } bands && Add(bands[^1], "from_months", 12)),
        new("daily_norm_half_an_hour_off", NearMiss, "overtime", false, (rules, at) => Add(rules[at], "daily_norm_minutes", 30)),
        new("overtime_percent_five_points_off", NearMiss, "overtime", false, (rules, at) => Add(Items(rules[at], "tiers")![0], "percent", 5)),
        new("tier_an_hour_longer", NearMiss, "overtime", false, (rules, at) => Add(Items(rules[at], "tiers")![0], "minutes", 60)),
        new("supplement_a_krone_off", NearMiss, "inconvenient_hours", false, (rules, at) => Add(Items(rules[at], "windows")![0], "amount_per_hour", 1)),
        new("window_starts_an_hour_late", NearMiss, "inconvenient_hours", false, (rules, at) => Later(Items(rules[at], "windows")![0]!, minutes: 60)),
        new("pension_percent_half_a_point_off", NearMiss, "pension", false, (rules, at) => Add(rules[at], "employer_percent", 0.5m)),
        new("qualifying_period_three_months_longer", NearMiss, "pension", false, (rules, at) =>
            Number(rules[at], "qualifying_months") > 0 && Add(rules[at], "qualifying_months", 3)),
        new("percent_half_a_point_off", NearMiss, "holiday_allowance", false, (rules, at) => Add(rules[at], "percent", 0.5m)),
        new("percent_half_a_point_off", NearMiss, "special_savings", false, (rules, at) => Add(rules[at], "percent", 0.5m)),

        // The agreement's own figure, in the wrong place.
        new("rates_in_each_others_bands", Misplaced, "minimum_wage", false, (rules, at) =>
            Items(rules[at], "bands") is { Count: > 1 } bands && Swap(bands[0], "hourly_rate", bands[^1], "hourly_rate")),
        new("another_years_rates", Misplaced, "minimum_wage", false, (rules, at) => FromAnotherRule(rules, at, "bands")),
        new("overtime_percents_swapped", Misplaced, "overtime", false, (rules, at) =>
            Items(rules[at], "tiers") is { Count: > 1 } tiers && Swap(tiers[0], "percent", tiers[^1], "percent")),
        new("sunday_given_the_weekday_percent", Misplaced, "overtime", false, (rules, at) =>
        {
            var weekday = Number(Items(rules[at], "tiers")![0], "percent");
            if (weekday == Number(rules[at], "sunday_percent"))
            {
                return false;
            }

            rules[at]!["sunday_percent"] = weekday;
            return true;
        }),
        new("supplements_swapped", Misplaced, "inconvenient_hours", false, (rules, at) =>
            Items(rules[at], "windows") is { Count: > 1 } windows && Swap(windows[0], "amount_per_hour", windows[1], "amount_per_hour")),
        new("window_back_to_front", Misplaced, "inconvenient_hours", false, (rules, at) =>
        {
            var window = Items(rules[at], "windows")![0];
            return Swap(window, "from", window, "to");
        }),
        new("another_years_supplements", Misplaced, "inconvenient_hours", false, (rules, at) => FromAnotherRule(rules, at, "windows")),
        new("shares_swapped", Misplaced, "pension", false, (rules, at) =>
            Number(rules[at], "employee_percent") is not null && Swap(rules[at], "employer_percent", rules[at], "employee_percent")),
        new("total_as_the_employers_share", Misplaced, "pension", false, (rules, at) =>
            Number(rules[at], "employee_percent") is { } employee && Add(rules[at], "employer_percent", employee)),
        new("another_periods_percent", Misplaced, "pension", false, (rules, at) => FromAnotherRule(rules, at, "employer_percent")),
        new("another_periods_percent", Misplaced, "holiday_allowance", false, (rules, at) => FromAnotherRule(rules, at, "percent")),
        new("another_periods_percent", Misplaced, "special_savings", false, (rules, at) => FromAnotherRule(rules, at, "percent")),

        // Something left out.
        new("top_band_left_out", LeftOut, "minimum_wage", false, (rules, at) => RemoveLast(Items(rules[at], "bands"), atLeast: 2)),
        new("first_tier_left_out", LeftOut, "overtime", false, (rules, at) =>
        {
            if (Items(rules[at], "tiers") is not { Count: > 1 } tiers)
            {
                return false;
            }

            tiers.RemoveAt(0);
            return true;
        }),
        new("window_left_out", LeftOut, "inconvenient_hours", false, (rules, at) => RemoveLast(Items(rules[at], "windows"), atLeast: 2)),
        new("weekday_left_out", LeftOut, "inconvenient_hours", false, (rules, at) =>
            RemoveLast(Items(rules[at], "windows")!.Select(window => Items(window, "days")).FirstOrDefault(days => days is { Count: > 1 }), atLeast: 2)),
        new("no_qualifying_period", LeftOut, "pension", false, (rules, at) =>
        {
            if (Number(rules[at], "qualifying_months") is not > 0)
            {
                return false;
            }

            rules[at]!["qualifying_months"] = 0;
            return true;
        }),
        new("rule_left_out", LeftOut, null, false, (rules, at) =>
        {
            rules.RemoveAt(at);
            return true;
        }),

        // The wrong days.
        new("starts_a_month_late", WrongDays, null, false, (rules, at) =>
        {
            rules[at]!["from"] = Day(rules[at], "from")!.Value.AddMonths(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }),
        new("starts_when_another_period_starts", WrongDays, null, false, (rules, at) =>
        {
            // A date the agreement prints, for the wrong rule: the later period's first day, or the earlier one's.
            var other = at + 1 < rules.Count ? at + 1 : at - 1;
            if (other < 0 || Day(rules[at], "until") is { } until && Day(rules[other], "from") >= until)
            {
                return false;
            }

            rules[at]!["from"] = rules[other]!["from"]!.GetValue<string>();
            return true;
        }),
        new("ends_a_month_early", WrongDays, null, false, (rules, at) =>
        {
            if (Day(rules[at], "until") is not { } until)
            {
                return false;
            }

            rules[at]!["until"] = until.AddMonths(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }),
        new("never_ends", WrongDays, null, false, (rules, at) =>
        {
            if (Day(rules[at], "until") is null)
            {
                return false;
            }

            rules[at]!["until"] = null;
            return true;
        }),

        // The evidence.
        new("word_added_to_the_quote", Evidence, null, true, (rules, at) =>
        {
            var quote = rules[at]!["quote"]!.GetValue<string>();
            var space = quote.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0)
            {
                return false;
            }

            rules[at]!["quote"] = quote.Insert(space, " altid");
            return true;
        }),
        new("clause_that_does_not_exist", Evidence, null, true, (rules, at) =>
        {
            rules[at]!["clause_id"] = "§ 99, stk. 9";
            return true;
        }),
        new("quote_from_another_clause", Evidence, null, true, (rules, at) =>
        {
            // The quote is real, and filed under a clause that exists and does not hold it.
            var named = rules[at]!["clause_id"]!.GetValue<string>();
            var other = Items(rules[at], "also_from")!.Select(clause => clause!.GetValue<string>())
                .Concat(rules.Select(rule => rule!["clause_id"]!.GetValue<string>()))
                .FirstOrDefault(clause => clause != named);
            if (other is null)
            {
                return false;
            }

            rules[at]!["clause_id"] = other;
            return true;
        }),
    ];

    /// <summary>Plants every mistake in every rule it fits, in every agreement, and says what became of each.</summary>
    public static async Task<PlantedResults> RunAsync(IChatClient recorded, string modelId, Func<AgreementSpec, bool>? only = null)
    {
        // The agreements do not depend on each other, so they are worked through at the same time.
        var perAgreement = await Task.WhenAll(Evals.Agreements
            .Where(agreement => only?.Invoke(agreement.Spec) ?? true)
            .Select(agreement => Task.Run(() => RunAsync(recorded, modelId, agreement.Spec))));
        var outcomes = perAgreement.SelectMany(found => found).ToList();
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
        return new(modelId, ExtractionPrompt.Version, totals, [.. outcomes.Where(outcome => outcome.Outcome is "reported" or "silent")]);
    }

    private static async Task<List<PlantedOutcome>> RunAsync(IChatClient recorded, string modelId, AgreementSpec spec)
    {
        var outcomes = new List<PlantedOutcome>();
        var agreement = Corpus.Generate(spec);
        var read = ClauseSegmenter.Segment(await new PdfLayoutReader().ReadAsync(AgreementPdf.Render(agreement.Document)));
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

    private static JsonArray? Items(JsonNode? node, string name) => node?[name] as JsonArray;

    private static decimal? Number(JsonNode? node, string name) =>
        node?[name] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number ? value.GetValue<decimal>() : null;

    private static DateOnly? Day(JsonNode? node, string name) =>
        node?[name]?.GetValue<string>() is { } text ? DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    private static TimeOnly Clock(JsonNode node, string name) =>
        TimeOnly.ParseExact(node[name]!.GetValue<string>(), "HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Moves the time a window starts.</summary>
    private static bool Later(JsonNode window, int minutes)
    {
        window["from"] = Clock(window, "from").AddMinutes(minutes).ToString("HH\\:mm", CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Adds to a number. False when there is no number there.</summary>
    private static bool Add(JsonNode? node, string name, decimal amount)
    {
        if (Number(node, name) is not { } value)
        {
            return false;
        }

        node![name] = value + amount;
        return true;
    }

    /// <summary>Exchanges two values. False when they are the same, since nothing would change.</summary>
    private static bool Swap(JsonNode? first, string firstName, JsonNode? second, string secondName)
    {
        var (one, other) = (first![firstName]!.DeepClone(), second![secondName]!.DeepClone());
        if (JsonNode.DeepEquals(one, other))
        {
            return false;
        }

        first[firstName] = other;
        second[secondName] = one;
        return true;
    }

    /// <summary>Takes a value from the nearest other rule in the answer that states it differently.</summary>
    private static bool FromAnotherRule(JsonArray rules, int at, string name)
    {
        var mine = rules[at]![name];
        var other = Enumerable.Range(0, rules.Count)
            .Where(index => index != at)
            .OrderBy(index => Math.Abs(index - at))
            .Select(index => rules[index]![name])
            .FirstOrDefault(value => value is not null and not JsonArray { Count: 0 } && !JsonNode.DeepEquals(value, mine));
        if (other is null || mine is null or JsonArray { Count: 0 })
        {
            return false;
        }

        rules[at]![name] = other.DeepClone();
        return true;
    }

    private static bool RemoveLast(JsonArray? items, int atLeast)
    {
        if (items is null || items.Count < atLeast)
        {
            return false;
        }

        items.RemoveAt(items.Count - 1);
        return true;
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

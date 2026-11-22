using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Overens.Documents;
using Overens.Gateway;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Reading;
using Overens.Rules;
using Overens.Synthetic;

namespace Overens.Evals;

/// <summary>One rule put in front of a reviewer, as the console would show it.</summary>
/// <param name="Card">The card's number in the study.</param>
/// <param name="Rule">What the rule says, in words.</param>
/// <param name="Clauses">The clauses it rests on: the one it quotes first, with the quote marked.</param>
public sealed record ReviewCard(int Card, string Agreement, string Kind, string Period, string Rule, IReadOnlyList<(string Id, string Text, string? Quote)> Clauses);

/// <summary>What a card is, kept apart from the page the reviewer sees.</summary>
/// <param name="Wrong">Whether the rule is not what the agreement states.</param>
/// <param name="What">For a wrong rule, the mistake: one a model made, or one planted.</param>
public sealed record ReviewKey(int Card, string Agreement, bool Wrong, string What);

/// <summary>A rule that could be put on a card, and what it is.</summary>
public sealed record ReviewCandidate(string Agreement, ReadAgreement Read, ProposedRule Rule, string What);

/// <summary>Every rule a study could be made of.</summary>
/// <param name="Right">Rules a model read correctly, that pass the checks and a second reading.</param>
/// <param name="Made">Wrong rules a model made that pass the checks and a second reading.</param>
/// <param name="Planted">Planted wrong rules that pass the checks in silence.</param>
public sealed record ReviewCandidates(IReadOnlyList<ReviewCandidate> Right, IReadOnlyList<ReviewCandidate> Made, IReadOnlyList<ReviewCandidate> Planted);

/// <summary>How a reviewer did.</summary>
/// <param name="Missed">The wrong cards the reviewer did not reject.</param>
public sealed record ReviewScore(string Reviewer, int Wrong, int Caught, int Right, int RejectedRight, int Unanswered, IReadOnlyList<int> Missed);

/// <summary>
/// The reviewer is the last guard against a wrong rule that every check passes, and how good a
/// guard is not measured. This builds the measurement for people to take: a page of rule
/// cards, each beside its clause, for a reviewer to approve or reject, and a key kept apart.
///
/// The wrong cards are mistakes that pass the checks: the ones the models made on the unseen
/// agreements and that a second reading did not catch, and planted ones of the kinds that pass
/// in silence. In use a second reading would stop most planted ones, so they stand for mistakes
/// both models make. The right cards are rules the models read correctly.
/// </summary>
public static class ReviewStudy
{
    public static async Task<(IReadOnlyList<ReviewCard> Cards, IReadOnlyList<ReviewKey> Key)> BuildAsync(string root = "", int seed = 2026, int right = 24, int planted = 9) =>
        Pick(await GatherAsync(root), seed, right, planted);

    /// <summary>Every rule a card could be made of. The slow part: it reads every agreement and plants every mistake.</summary>
    /// <param name="root">The repository's root.</param>
    /// <param name="silent">The planted study's silent rules, when it has been run already; it is run here otherwise.</param>
    public static async Task<ReviewCandidates> GatherAsync(string root = "", IReadOnlyList<(AgreementSpec Spec, ReadAgreement Read, ProposedRule Rule, string Mistake)>? silent = null)
    {
        var (first, second) = (Models.Sonnet, Models.Haiku);
        // Rules the models read, after the checks and a second reading: right ones, and wrong ones that got through.
        var read = await Task.WhenAll((await Agreements(root)).Select(agreement => Task.Run(async () =>
        {
            var (set, id, document, truth) = agreement;
            var independent = set is "independent" or "unseen";
            string Recordings(string model) => independent ? Path.Combine(root, Independent.Recordings(model)) : Path.Combine(root, "evals", "recordings", ExtractionPrompt.Version, model);
            var both = SecondReading.Apply(await Extract(Recordings(first), first, document), await Extract(Recordings(second), second, document));
            var expected = truth.Rules.Select(ExtractionEval.Meaning).ToHashSet();
            var sound = both.Rules.Where(rule => rule.Problems.Count == 0).ToList();
            return (
                Right: sound.Where(rule => expected.Contains(ExtractionEval.Meaning(rule.Rule))).Select(rule => new ReviewCandidate(id, document, rule, "")).ToList(),
                Wrong: sound.Where(rule => !expected.Contains(ExtractionEval.Meaning(rule.Rule))).Select(rule => new ReviewCandidate(id, document, rule, "a model's own mistake")).ToList());
        })));

        if (silent is null)
        {
            using IChatClient recorded = new ReplayChatClient(null, Path.Combine(root, "evals", "recordings", ExtractionPrompt.Version, first));
            silent = (await PlantedMistakes.RunWithSilentAsync(recorded, first)).Silent;
        }

        return new(
            [.. read.SelectMany(agreement => agreement.Right)],
            [.. read.SelectMany(agreement => agreement.Wrong)],
            [.. silent.Select(planted => new ReviewCandidate(planted.Spec.Id, planted.Read, planted.Rule, $"planted: {planted.Mistake}"))]);
    }

    /// <summary>The study's cards, chosen by a seed: the same seed gives the same study.</summary>
    public static (IReadOnlyList<ReviewCard> Cards, IReadOnlyList<ReviewKey> Key) Pick(ReviewCandidates candidates, int seed = 2026, int right = 24, int planted = 9)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var random = new Random(seed);
        // One planted mistake of each kind that passes in silence, as far as there are kinds.
        var kinds = candidates.Planted.GroupBy(candidate => candidate.What).OrderBy(group => group.Key, StringComparer.Ordinal).ToList();
        var plantedPicked = kinds.OrderBy(_ => random.Next()).Take(planted).Select(group => group.ElementAt(random.Next(group.Count())));
        var picked = candidates.Right.OrderBy(_ => random.Next()).Take(right).Select(candidate => (Candidate: candidate, Wrong: false))
            .Concat(candidates.Made.Concat(plantedPicked).Select(candidate => (Candidate: candidate, Wrong: true)))
            .OrderBy(_ => random.Next())
            .ToList();
        return (
            [.. picked.Select((each, at) => Card(at + 1, each.Candidate.Agreement, each.Candidate.Read, each.Candidate.Rule))],
            [.. picked.Select((each, at) => new ReviewKey(at + 1, each.Candidate.Agreement, each.Wrong, each.Candidate.What))]);
    }

    /// <summary>Writes the page for the reviewer and, beside it, the key that is not for the reviewer.</summary>
    public static async Task WriteAsync(string directory, IReadOnlyList<ReviewCard> cards, IReadOnlyList<ReviewKey> key)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "review.html"), Page(cards));
        await File.WriteAllTextAsync(Path.Combine(directory, "key.json"), JsonSerializer.Serialize(key, Indented) + "\n");
    }

    /// <summary>Scores a reviewer's answers, as the page saves them, against the key.</summary>
    public static ReviewScore Score(IReadOnlyList<ReviewKey> key, string answers)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var document = JsonDocument.Parse(answers);
        var reviewer = document.RootElement.TryGetProperty("reviewer", out var name) ? name.GetString() ?? "" : "";
        var decisions = document.RootElement.GetProperty("answers").EnumerateArray()
            .ToDictionary(answer => answer.GetProperty("card").GetInt32(), answer => answer.GetProperty("decision").GetString());
        string? Decision(ReviewKey card) => decisions.GetValueOrDefault(card.Card) is { } said && said is "approve" or "reject" ? said : null;
        return new(
            reviewer,
            key.Count(card => card.Wrong),
            key.Count(card => card.Wrong && Decision(card) == "reject"),
            key.Count(card => !card.Wrong),
            key.Count(card => !card.Wrong && Decision(card) == "reject"),
            key.Count(card => Decision(card) is null),
            [.. key.Where(card => card.Wrong && Decision(card) != "reject").Select(card => card.Card)]);
    }

    private static readonly JsonSerializerOptions Indented = new(EvalResults.Json) { WriteIndented = true };

    private static async Task<List<(string Set, string Id, ReadAgreement Read, RuleSet Truth)>> Agreements(string root)
    {
        var all = new List<(string, string, ReadAgreement, RuleSet)>();
        foreach (var (set, spec) in Evals.Agreements)
        {
            var generated = Corpus.Generate(spec);
            all.Add((set, spec.Id, await Read(generated.Document), generated.Truth));
        }

        foreach (var set in Independent.Sets)
        {
            foreach (var id in Independent.Ids(set, root))
            {
                var (document, truth) = Independent.Load(set, id, root);
                all.Add((set, id, await Read(document), truth));
            }
        }

        return all;
    }

    private static Task<ReadAgreement> Read(AgreementDocument document) => ReadOnce.Agreement(document);

    private static async Task<ExtractionResult> Extract(string recordings, string model, ReadAgreement read)
    {
        using IChatClient recorded = new ReplayChatClient(null, recordings);
        return await new RuleExtractor(recorded, model).ExtractAsync(read);
    }

    private static ReviewCard Card(int number, string agreement, ReadAgreement read, ProposedRule proposed)
    {
        var rule = proposed.Rule;
        var clauses = read.Sections.SelectMany(section => section.Clauses).ToDictionary(clause => clause.Id);
        var home = read.Sections.FirstOrDefault(section => section.Clauses.Any(clause => clause.Id == rule.Source.ClauseId));
        var shown = new List<(string, string, string?)>();
        if (clauses.TryGetValue(rule.Source.ClauseId, out var quoted))
        {
            shown.Add((quoted.Id, ExtractionPrompt.Text(quoted), rule.Source.Quote));
        }

        foreach (var other in (home?.Clauses ?? []).Select(clause => clause.Id).Concat(proposed.AlsoFrom).Distinct().Where(id => id != rule.Source.ClauseId && clauses.ContainsKey(id)))
        {
            shown.Add((other, ExtractionPrompt.Text(clauses[other]), null));
        }

        return new(number, agreement, ExtractionEval.KindOf(rule), Danish.Date(rule.InForce.From) + (rule.InForce.Until is { } until ? $" – {Danish.Date(until.AddDays(-1))}" : " –"), Describe(rule), shown);
    }

    /// <summary>A rule in words, as a reviewer reads it.</summary>
    public static string Describe(Rule rule) => rule switch
    {
        MinimumWageRule wage => string.Join("; ", wage.Bands.Select(band => $"from {band.FromMonths} months: {Danish.Kroner(band.HourlyRate)} an hour")),
        OvertimeRule overtime => $"daily norm {Hours(overtime.DailyNormHours)}; "
            + string.Join("; ", overtime.Tiers.Select((tier, at) => tier.Hours is { } hours ? $"{(at == 0 ? "first" : "next")} {Hours(hours)} of overtime: {Danish.Percent(tier.Percent)}" : $"overtime after that: {Danish.Percent(tier.Percent)}"))
            + $"; every hour on a Sunday: {Danish.Percent(overtime.SundayPercent)}",
        InconvenientHoursRule hours => string.Join("; ", hours.Windows.Select(window =>
            $"{string.Join(", ", window.Days.Select(day => day.ToString()[..3]))} {window.From:HH\\:mm}–{(window.To == TimeOnly.MinValue ? "24:00" : window.To.ToString("HH\\:mm", CultureInfo.InvariantCulture))}: {Danish.Kroner(window.AmountPerHour)} an hour")),
        PensionRule pension => $"employer {Danish.Percent(pension.EmployerPercent)}, employee {Danish.Percent(pension.EmployeePercent)}, after {pension.QualifyingMonths} months",
        HolidayAllowanceRule holiday => Danish.Percent(holiday.Percent) + " of pay",
        SpecialSavingsRule savings => Danish.Percent(savings.Percent) + " of pay",
        _ => rule.ToString(),
    };

    private static string Hours(decimal hours)
    {
        var minutes = (int)(hours * 60);
        return minutes % 60 == 0 ? $"{minutes / 60} hours" : $"{minutes / 60} hours {minutes % 60} minutes";
    }

    private static string Page(IReadOnlyList<ReviewCard> cards)
    {
        static string Html(string text) => WebUtility.HtmlEncode(text);
        static string Marked(string text, string? quote)
        {
            var at = quote is null ? -1 : text.IndexOf(quote, StringComparison.Ordinal);
            return at < 0 ? Html(text) : Html(text[..at]) + "<mark>" + Html(quote!) + "</mark>" + Html(text[(at + quote!.Length)..]);
        }

        var body = new StringBuilder();
        foreach (var card in cards)
        {
            body.Append(CultureInfo.InvariantCulture, $"<section class=\"card\" data-card=\"{card.Card}\"><h2>Card {card.Card} <span>{Html(card.Kind.Replace('_', ' '))} · {Html(card.Agreement)} · {Html(card.Period)}</span></h2>");
            body.Append(CultureInfo.InvariantCulture, $"<p class=\"rule\">{Html(card.Rule)}</p>");
            foreach (var (id, text, quote) in card.Clauses)
            {
                body.Append(CultureInfo.InvariantCulture, $"<div class=\"clause\"><b>{Html(id)}</b><pre>{Marked(text, quote)}</pre></div>");
            }

            body.Append(CultureInfo.InvariantCulture, $"<div class=\"decide\"><button data-decision=\"approve\">Approve</button><button data-decision=\"reject\">Reject</button><input placeholder=\"Why, if you reject (optional)\"></div></section>\n");
        }

        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Overens review study</title>
            <style>
            :root { --paper: #fbf9f4; --ink: #1d2a24; --rule: #d9d3c4; --mark: #f3e6a6; --ok: #1f6f4a; --no: #9b2c2c; }
            body { margin: 0 auto; max-width: 52rem; padding: 1rem; background: var(--paper); color: var(--ink); font: 16px/1.5 system-ui, sans-serif; }
            h1 { font-size: 1.4rem; } .card { border-top: 1px solid var(--rule); padding: 1rem 0; }
            h2 { font-size: 1.05rem; margin: 0 0 .5rem; } h2 span { font-weight: normal; color: #5b665f; }
            .rule { font-weight: 600; } pre { white-space: pre-wrap; font: 15px/1.5 Georgia, serif; margin: .25rem 0 .75rem; }
            mark { background: var(--mark); } .decide { display: flex; gap: .5rem; flex-wrap: wrap; }
            button { font: inherit; padding: .35rem .9rem; border: 1px solid var(--rule); background: #fff; border-radius: 4px; cursor: pointer; }
            .card[data-said="approve"] button[data-decision="approve"] { background: var(--ok); color: #fff; }
            .card[data-said="reject"] button[data-decision="reject"] { background: var(--no); color: #fff; }
            input { flex: 1 1 14rem; font: inherit; padding: .3rem; } #bar { position: sticky; top: 0; background: var(--paper); padding: .5rem 0; border-bottom: 1px solid var(--rule); }
            </style></head><body>
            <h1>Review study</h1>
            <p>Each card is a rule a system read out of a collective agreement, shown beside the clauses it rests on, with the sentence it quotes marked. Approve a rule if it is what the agreement states for those days. Reject it if anything in it is not. Take the time you would take at work. Some rules are right and some are not; the page does not say how many.</p>
            <div id="bar">Your name or initials <input id="reviewer" size="12"> <span id="count"></span> <button id="save">Download answers</button></div>
            {{body}}
            <script>
            const key = "overens-review-study";
            let state = { reviewer: "", answers: {} };
            try { state = JSON.parse(localStorage.getItem(key)) || state; } catch (e) {}
            const keep = () => { try { localStorage.setItem(key, JSON.stringify(state)); } catch (e) {} count(); };
            const count = () => { document.getElementById("count").textContent = Object.keys(state.answers).length + " of {{cards.Count}} decided"; };
            document.getElementById("reviewer").value = state.reviewer;
            document.getElementById("reviewer").addEventListener("input", e => { state.reviewer = e.target.value; keep(); });
            document.querySelectorAll(".card").forEach(card => {
              const id = card.dataset.card, note = card.querySelector("input");
              const saved = state.answers[id]; if (saved) { card.dataset.said = saved.decision; note.value = saved.note || ""; }
              card.querySelectorAll("button").forEach(button => button.addEventListener("click", () => {
                card.dataset.said = button.dataset.decision; state.answers[id] = { decision: button.dataset.decision, note: note.value }; keep(); }));
              note.addEventListener("input", () => { if (state.answers[id]) { state.answers[id].note = note.value; keep(); } });
            });
            document.getElementById("save").addEventListener("click", () => {
              const answers = Object.entries(state.answers).map(([card, a]) => ({ card: Number(card), decision: a.decision, note: a.note }));
              const file = new Blob([JSON.stringify({ reviewer: state.reviewer, answers }, null, 2)], { type: "application/json" });
              const link = document.createElement("a"); link.href = URL.createObjectURL(file); link.download = "answers.json"; link.click();
            });
            count();
            </script></body></html>
            """;
    }
}

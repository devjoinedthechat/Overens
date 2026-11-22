using System.Text.Json;
using Overens.Gateway;
using Overens.Rules;

namespace Overens.Evals.Tests;

/// <summary>
/// The two results that were recorded through Claude Code and not the API: the agreements
/// the generator did not write, and the same extraction asked several times. Both replay
/// from recordings like the rest.
/// </summary>
public sealed class IndependentAndVarianceTests
{
    public static TheoryData<string> Models => [Gateway.Models.Sonnet, Gateway.Models.Haiku];

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Overens.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<EvalResults>>> Readings = new();

    /// <summary>One replay per model, for every test that reads it.</summary>
    private static Task<EvalResults> Independently(string model) => Readings.GetOrAdd(model, _ => new(async () =>
    {
        using var recorded = new ReplayChatClient(null, Path.Combine(Root(), Independent.Recordings(model)));
        return await Independent.RunAsync(recorded, model, Root());
    })).Value;

    [Fact]
    public void The_hand_written_agreements_state_usable_rule_sets_and_quote_their_own_clauses()
    {
        var all = Independent.Sets.SelectMany(set => Independent.Ids(set, Root()).Select(id => (Set: set, Id: id))).ToList();
        Assert.Equal(8, all.Count);
        foreach (var (set, id) in all)
        {
            var (document, truth) = Independent.Load(set, id, Root());

            Assert.Empty(RuleSetValidator.Validate(truth));
            Assert.Equal(6, truth.Rules.Select(rule => rule.GetType()).Distinct().Count());
            var clauses = document.Sections.Concat(document.Protocols).SelectMany(section => section.Clauses).ToDictionary(clause => clause.Id, Documents.ClauseText.Of);
            Assert.All(truth.Rules, rule => Assert.Contains(rule.Source.Quote, clauses[rule.Source.ClauseId], StringComparison.Ordinal));
            // The answers they come with are answers the engine can pay by.
            Assert.NotEmpty(Independent.Pay(id, truth).Errors);
        }
    }

    [Theory]
    [MemberData(nameof(Models))]
    public async Task The_published_results_on_the_hand_written_agreements_are_what_the_recorded_answers_give(string model)
    {
        var published = await File.ReadAllTextAsync(Path.Combine(Root(), "docs", "eval-report", "results", $"independent-{Ingestion.Extraction.ExtractionPrompt.Version}", $"{model}.json"));

        Assert.Equal(published, JsonSerializer.Serialize(await Independently(model), EvalResults.Json) + "\n");
    }

    [Fact]
    public async Task The_agreements_the_prompt_was_fixed_against_are_read_whole_and_the_unseen_ones_are_not()
    {
        // These figures are the report's. If a change moves them, the report must move with them.
        var sonnet = await Independently(Gateway.Models.Sonnet);
        var haiku = await Independently(Gateway.Models.Haiku);
        foreach (var results in new[] { sonnet, haiku })
        {
            var (rules, pay) = (results.Total("independent"), results.Pay("independent"));
            Assert.Equal((48, 48, 0, 0), (rules.Expected, rules.Correct, rules.Flagged, rules.WrongAndUnflagged));
            Assert.Equal((288, 288), (pay.Cases, pay.Right));
        }

        static (int, int, int, int) Shape(KindScore score) => (score.Expected, score.Correct, score.Flagged, score.WrongAndUnflagged);
        Assert.Equal((51, 40, 13, 7), Shape(sonnet.Total("unseen")));
        Assert.Equal((51, 30, 14, 2), Shape(haiku.Total("unseen")));
        // A wrong holiday allowance on one agreement is approved by the checks, and its payslips are judged on it.
        Assert.All(new[] { sonnet, haiku }, results => Assert.Equal(new PayScore(288, 0, 10, 26, 252), results.Pay("unseen")));
    }

    [Theory]
    [InlineData("claude-haiku-5-5", 5)]
    [InlineData("claude-sonnet-5-5", 3)]
    public async Task Asked_again_a_model_finds_every_rule_every_time_and_nothing_wrong_passes(string model, int passes)
    {
        var variance = await Variance.RunAsync(null, model, passes, Root());

        var published = await File.ReadAllTextAsync(Path.Combine(Root(), "docs", "eval-report", "results", $"variance-{Ingestion.Extraction.ExtractionPrompt.Version}", $"{model}.json"));
        Assert.Equal(published, JsonSerializer.Serialize(variance, EvalResults.Json) + "\n");
        Assert.All(variance.Agreements, agreement =>
        {
            Assert.All(agreement.Found, found => Assert.Equal(agreement.Expected, found));
            Assert.All(agreement.WrongAndUnflagged, wrong => Assert.Equal(0, wrong));
        });
        // What does vary is Haiku, and it stays inside the long agreements and the one that has every twist.
        Assert.All(variance.Agreements.Where(agreement => agreement.Set != "long" && agreement.Agreement != "twist-all"), agreement => Assert.True(agreement.SameEveryPass, agreement.Agreement));
        if (model == Gateway.Models.Sonnet)
        {
            Assert.All(variance.Agreements, agreement => Assert.True(agreement.SameEveryPass, agreement.Agreement));
        }
    }
}

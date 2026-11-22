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

    private static async Task<EvalResults> Independently(string model)
    {
        using var recorded = new ReplayChatClient(null, Path.Combine(Root(), Independent.Recordings(model)));
        return await Independent.RunAsync(recorded, model, Root());
    }

    [Fact]
    public void The_hand_written_agreements_state_usable_rule_sets_and_quote_their_own_clauses()
    {
        foreach (var id in Independent.Ids)
        {
            var (document, truth) = Independent.Load(id, Root());

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
        var published = await File.ReadAllTextAsync(Path.Combine(Root(), "docs", "eval-report", "results", "independent", $"{model}.json"));

        Assert.Equal(published, JsonSerializer.Serialize(await Independently(model), EvalResults.Json) + "\n");
    }

    [Fact]
    public async Task On_the_hand_written_agreements_no_payslip_is_misjudged_and_what_went_wrong_is_as_reported()
    {
        var sonnet = (await Independently(Gateway.Models.Sonnet)).Total("independent");
        var haiku = (await Independently(Gateway.Models.Haiku)).Total("independent");

        // Sonnet reads every rule, and nine of them are flagged though they are right.
        Assert.Equal((48, 48, 0, 9), (sonnet.Expected, sonnet.Correct, sonnet.WrongAndUnflagged, sonnet.RightButFlagged));
        // Haiku misses four, and two wrong rules pass the checks: a minimum wage without its seniority steps.
        // This is the report's known failure. If it changes, the report must.
        Assert.Equal((48, 44, 2, 5), (haiku.Expected, haiku.Correct, haiku.WrongAndUnflagged, haiku.RightButFlagged));
        foreach (var model in new[] { Gateway.Models.Sonnet, Gateway.Models.Haiku })
        {
            var pay = (await Independently(model)).Pay("independent");
            Assert.Equal((0, 0), (pay.MissedErrors, pay.FalseAlarms));
            Assert.Equal((144, 144), (pay.Right, pay.NotChecked));
        }
    }

    [Theory]
    [InlineData("claude-haiku-5-5", 5)]
    [InlineData("claude-sonnet-5-5", 3)]
    public async Task Asked_again_a_model_finds_every_rule_every_time_and_nothing_wrong_passes(string model, int passes)
    {
        var variance = await Variance.RunAsync(null, model, passes, Root());

        var published = await File.ReadAllTextAsync(Path.Combine(Root(), "docs", "eval-report", "results", "variance", $"{model}.json"));
        Assert.Equal(published, JsonSerializer.Serialize(variance, EvalResults.Json) + "\n");
        Assert.All(variance.Agreements, agreement =>
        {
            Assert.All(agreement.Found, found => Assert.Equal(agreement.Expected, found));
            Assert.All(agreement.WrongAndUnflagged, wrong => Assert.Equal(0, wrong));
        });
        // What does vary stays inside the long agreements.
        Assert.All(variance.Agreements.Where(agreement => agreement.Set != "long"), agreement => Assert.True(agreement.SameEveryPass, agreement.Agreement));
    }
}

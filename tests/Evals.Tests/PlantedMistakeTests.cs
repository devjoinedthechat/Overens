using System.Text.Json;
using Overens.Gateway;
using Overens.Ingestion.Extraction;
using Overens.Rules;
using Overens.Rules.Tests;
using Overens.Synthetic;

namespace Overens.Evals.Tests;

/// <summary>
/// The model-free checks, measured on wrong answers. Every recorded answer is spoiled one
/// way at a time and run through extraction again. These tests hold the published figures to
/// a replay, and set the bar for the kinds of mistake the checks are there to stop.
/// </summary>
public sealed class PlantedMistakeTests
{
    private static readonly Lazy<Task<PlantedResults>> Study = new(async () =>
    {
        using var recorded = new ReplayChatClient(null, Path.Combine(Root(), "evals", "recordings", ExtractionPrompt.Version, Models.Sonnet));
        return await PlantedMistakes.RunAsync(recorded, Models.Sonnet);
    });

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Overens.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [Fact]
    public async Task The_published_results_are_what_the_recorded_answers_give()
    {
        var published = await File.ReadAllTextAsync(Path.Combine(Root(), "docs", "eval-report", "results", $"planted-{ExtractionPrompt.Version}", $"{Models.Sonnet}.json"));

        Assert.Equal(published, JsonSerializer.Serialize(await Study.Value, EvalResults.Json) + "\n");
    }

    [Fact]
    public async Task The_figures_in_the_report_and_the_readme_are_the_studys_own()
    {
        var study = await Study.Value;
        var all = study.Total();
        var misplaced = study.Total(PlantedMistakes.Misplaced);
        var leftOut = study.Total(PlantedMistakes.LeftOut);
        string[] figures =
        [
            $"| All | {all.Stopped:N0} of {all.Planted:N0} | {all.Reported} | {all.Silent} |",
            $"| The agreement's own figure in the wrong place | {misplaced.Stopped} of {misplaced.Planted} | 0 | {misplaced.Silent} |",
            $"| Something left out | {leftOut.Stopped} of {leftOut.Planted} | {leftOut.Reported} | {leftOut.Silent} |",
            $"{study.Totals.Count} kinds of mistake",
        ];
        var report = await File.ReadAllTextAsync(Path.Combine(Root(), "docs", "eval-report", "README.md"));
        var readme = await File.ReadAllTextAsync(Path.Combine(Root(), "README.md"));

        Assert.All(figures, figure => Assert.Contains(figure, report, StringComparison.Ordinal));
        Assert.Contains($"{all.Planted:N0} mistakes of {study.Totals.Count} kinds", readme, StringComparison.Ordinal);
        Assert.Contains($"| The agreement's own figure in the wrong place | {misplaced.Stopped} of {misplaced.Planted} | 0 | {misplaced.Silent} |", readme, StringComparison.Ordinal);
        Assert.Contains($"which stop {all.Stopped:N0} since the fixes", readme, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PlantedMistakes.NotPrinted)]
    [InlineData(PlantedMistakes.NearMiss)]
    [InlineData(PlantedMistakes.WrongDays)]
    [InlineData(PlantedMistakes.Evidence)]
    public async Task A_figure_a_day_or_a_quote_that_is_not_the_agreements_never_gets_through(string group)
    {
        var total = (await Study.Value).Total(group);

        Assert.InRange(total.Planted, 300, int.MaxValue);
        Assert.Equal(total.Planted, total.Stopped);
    }

    [Fact]
    public async Task A_rule_left_out_is_always_reported_to_the_reviewer()
    {
        var leftOut = (await Study.Value).Totals.Single(total => total.Mistake == "rule_left_out");

        Assert.Equal((200, 200), (leftOut.Planted, leftOut.Reported));
    }

    [Fact]
    public async Task Most_figures_put_in_the_wrong_place_are_stopped_and_the_rest_are_known()
    {
        var study = await Study.Value;
        var misplaced = study.Total(PlantedMistakes.Misplaced);

        // Three in four. The bar is where the checks stand, so that a change which loses ground fails here.
        Assert.True(misplaced.Stopped * 4 >= misplaced.Planted * 3, $"{misplaced.Stopped} of {misplaced.Planted} stopped.");
        // A figure that stands in a table under another date is always stopped.
        Assert.All(
            study.Totals.Where(total => total.Mistake is "another_years_rates" or "another_years_supplements"),
            total => Assert.Equal(total.Planted, total.Stopped));
    }

    [Fact]
    public async Task Every_mistake_was_planted_and_every_one_changed_something()
    {
        var study = await Study.Value;

        Assert.Equal(PlantedMistakes.All.Select(mistake => mistake.Name).Distinct().Order(), study.Totals.Select(total => total.Mistake).Order());
        Assert.All(study.Totals, total => Assert.True(total.Planted > 0, $"{total.Mistake} was never planted."));
        Assert.All(study.Totals, total => Assert.Equal(0, total.NoChange));
        Assert.Equal(study.Totals.Sum(total => total.Reported + total.Silent), study.NotStopped.Count);
    }

    [Fact]
    public void An_outcome_is_judged_by_what_a_reviewer_would_be_told()
    {
        var truth = Sample.Renewed;
        var pay = PayDataGenerator.Generate(truth, seed: 7, new(2026, 3, 1), months: 1, employees: 4);
        static ExtractionResult Proposed(IEnumerable<Rule> rules, string? flagged = null, params RuleProblem[] gaps) =>
            new([.. rules.Select(rule => new ProposedRule(rule, [], rule.Id == flagged ? [new(rule.Id, "flagged", "")] : []))], [], gaps);
        var untouched = Proposed(truth.Rules);
        var wrongHoliday = truth.Rules.Select(rule => rule is HolidayAllowanceRule { Id: "holiday" } holiday ? holiday with { Percent = 13m } : rule).ToList();
        var mistake = new Mistake("test", PlantedMistakes.Misplaced, null, RuleStaysRight: false, (_, _) => true);
        string Outcome(ExtractionResult extraction, Mistake? planted = null) =>
            PlantedMistakes.Judge("sample", "holiday_allowance", "2026-03-01", planted ?? mistake, truth, untouched, extraction, pay).Outcome;

        // A wrong rule with a problem on it cannot be approved.
        Assert.Equal("stopped", Outcome(Proposed(wrongHoliday, flagged: "holiday")));
        // A wrong rule that looks sound, and nothing said about it.
        var silent = PlantedMistakes.Judge("sample", "holiday_allowance", "2026-03-01", mistake, truth, untouched, Proposed(wrongHoliday), pay);
        Assert.Equal("silent", silent.Outcome);
        Assert.InRange(silent.PayslipsWrong, 1, pay.Cases.Count);
        // The same, with the run saying that something is missing.
        Assert.Equal("reported", Outcome(Proposed(wrongHoliday, null, new RuleProblem("holiday_allowance", "period_not_covered", "No rule of this kind applies from 2026-03-01."))));
        // A true rule gone, and whether the run says so.
        var without = truth.Rules.Where(rule => rule.Id != "holiday").ToList();
        Assert.Equal("silent", Outcome(Proposed(without)));
        Assert.Equal("reported", Outcome(Proposed(without, null, new RuleProblem("holiday_allowance", "period_not_covered", "No rule of this kind applies from 2026-03-01."))));
        // The true rules, as they were: nothing was changed.
        Assert.Equal("no_change", Outcome(untouched));
        // A right rule with spoiled evidence must still be flagged.
        var evidence = mistake with { RuleStaysRight = true };
        Assert.Equal("stopped", Outcome(Proposed(truth.Rules, flagged: "holiday"), evidence));
        Assert.Equal("silent", Outcome(untouched, evidence));
    }
}

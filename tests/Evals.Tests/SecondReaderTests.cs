using System.Text.Json;
using Overens.Gateway;
using Overens.Ingestion.Extraction;

namespace Overens.Evals.Tests;

/// <summary>What a second reading adds, held to the recordings and to the figures the report prints.</summary>
public sealed class SecondReaderTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Overens.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [Theory]
    [InlineData("claude-sonnet-5-5", "claude-haiku-5-5", 3, 5)]
    [InlineData("claude-haiku-5-5", "claude-sonnet-5-5", 5, 3)]
    public async Task The_published_results_are_what_the_recorded_answers_give(string first, string second, int firstPasses, int secondPasses)
    {
        var results = await SecondReaderEval.RunAsync(first, second, Root(), firstPasses, secondPasses);

        var published = await File.ReadAllTextAsync(Path.Combine(Root(), "docs", "eval-report", "results", $"second-reader-{ExtractionPrompt.Version}", $"{first}-then-{second}.json"));
        Assert.Equal(published, JsonSerializer.Serialize(results, EvalResults.Json) + "\n");

        // On the generated agreements, a second reading never flagged a correct rule, in any pairing of passes.
        Assert.Equal(firstPasses * secondPasses, results.Pairs.Count);
        Assert.All(results.Pairs, pair => Assert.Equal(0, pair.RightButFlagged));
        foreach (var set in new[] { "corpus", "challenge", "long", "independent" })
        {
            Assert.Equal(results.Total(set, false), results.Total(set, true));
        }

        if (first == Models.Sonnet)
        {
            // The figures in the report and in decision 12.
            var (alone, together) = (results.Total("unseen", false), results.Total("unseen", true));
            Assert.Equal((7, 3, 13, 14), (alone.WrongAndUnflagged, together.WrongAndUnflagged, alone.RightButFlagged, together.RightButFlagged));
        }
    }

    [Fact]
    public async Task The_report_prints_the_intervals_the_results_give()
    {
        var report = await File.ReadAllTextAsync(Path.Combine(Root(), "docs", "eval-report", "README.md"));
        var sonnet = JsonSerializer.Deserialize<EvalResults>(await File.ReadAllTextAsync(Path.Combine(Root(), "docs", "eval-report", "results", $"independent-{ExtractionPrompt.Version}", "claude-sonnet-5-5.json")), EvalResults.Json)!;
        var haiku = JsonSerializer.Deserialize<EvalResults>(await File.ReadAllTextAsync(Path.Combine(Root(), "docs", "eval-report", "results", $"independent-{ExtractionPrompt.Version}", "claude-haiku-5-5.json")), EvalResults.Json)!;

        foreach (var results in new[] { sonnet, haiku })
        {
            var unseen = results.Total("unseen");
            Assert.Contains(Interval.Of(unseen.Correct, unseen.Expected), report, StringComparison.Ordinal);
        }
    }
}

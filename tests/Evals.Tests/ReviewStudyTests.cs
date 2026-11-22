using Overens.Rules;

namespace Overens.Evals.Tests;

public sealed class ReviewStudyTests
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

    private static readonly Lazy<Task<ReviewCandidates>> Candidates = new(async () => await ReviewStudy.GatherAsync(Root(), (await PlantedRun.Sonnet.Value).Silent));

    private static readonly Lazy<Task<(IReadOnlyList<ReviewCard> Cards, IReadOnlyList<ReviewKey> Key)>> Study = new(async () => ReviewStudy.Pick(await Candidates.Value));

    [Fact]
    public async Task The_study_mixes_right_rules_with_wrong_ones_that_pass_every_check()
    {
        var (cards, key) = await Study.Value;

        Assert.Equal(cards.Select(card => card.Card), key.Select(card => card.Card));
        Assert.Equal(24, key.Count(card => !card.Wrong));
        // The three mistakes the models made that both the checks and the second reading let through, and nine planted ones.
        Assert.Equal(3, key.Count(card => card.What == "a model's own mistake"));
        Assert.Equal(9, key.Count(card => card.What.StartsWith("planted: ", StringComparison.Ordinal)));
        Assert.Equal(9, key.Where(card => card.Wrong).Select(card => card.What).Where(what => what.StartsWith("planted", StringComparison.Ordinal)).Distinct().Count());
        Assert.All(cards, card => Assert.NotEmpty(card.Clauses));
    }

    [Fact]
    public async Task The_same_seed_gives_the_same_study()
    {
        var (_, again) = ReviewStudy.Pick(await Candidates.Value);
        var (_, other) = ReviewStudy.Pick(await Candidates.Value, seed: 7);

        Assert.Equal((await Study.Value).Key, again);
        Assert.NotEqual((await Study.Value).Key, other);
    }

    [Fact]
    public async Task The_page_does_not_give_the_answers_away()
    {
        var (cards, key) = await Study.Value;
        var directory = Directory.CreateTempSubdirectory("review-study-").FullName;
        try
        {
            await ReviewStudy.WriteAsync(directory, cards, key);
            var page = await File.ReadAllTextAsync(Path.Combine(directory, "review.html"));

            Assert.DoesNotContain("planted", page, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mistake", page, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"wrong\"", page, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<mark>", page, StringComparison.Ordinal);
            Assert.Equal(cards.Count, page.Split("class=\"card\"").Length - 1);
            Assert.True(File.Exists(Path.Combine(directory, "key.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_reviewers_answers_are_scored_against_the_key()
    {
        ReviewKey[] key = [new(1, "a", true, "x"), new(2, "a", true, "y"), new(3, "a", false, ""), new(4, "a", false, ""), new(5, "a", true, "z")];
        const string Answers = """
            { "reviewer": "AB", "answers": [
              { "card": 1, "decision": "reject" }, { "card": 2, "decision": "approve" },
              { "card": 3, "decision": "reject" }, { "card": 4, "decision": "approve" } ] }
            """;

        var score = ReviewStudy.Score(key, Answers);

        Assert.Equal(("AB", 3, 1, 2, 1, 1), (score.Reviewer, score.Wrong, score.Caught, score.Right, score.RejectedRight, score.Unanswered));
        // Card 5 was left undecided: a wrong rule not rejected is a wrong rule let through.
        Assert.Equal([2, 5], score.Missed);
    }

    [Fact]
    public void A_rule_is_described_the_way_a_reviewer_reads_it()
    {
        var period = new Period(new(2026, 3, 1));
        var source = new Source("§ 1", "tekst");

        Assert.Equal("from 0 months: 140,00 kr. an hour; from 12 months: 148,50 kr. an hour",
            ReviewStudy.Describe(new MinimumWageRule("w", period, source, [new(0, 140.00m), new(12, 148.50m)])));
        Assert.Equal("daily norm 7 hours 24 minutes; first 2 hours of overtime: 50,0 pct.; overtime after that: 100,0 pct.; every hour on a Sunday: 100,0 pct.",
            ReviewStudy.Describe(new OvertimeRule("o", period, source, 7.4m, [new(2, 50), new(null, 100)], 100)));
        Assert.Equal("Mon, Tue 22:00–24:00: 45,00 kr. an hour",
            ReviewStudy.Describe(new InconvenientHoursRule("i", period, source, [new([DayOfWeek.Monday, DayOfWeek.Tuesday], new(22, 0), new(0, 0), 45.00m)])));
    }
}

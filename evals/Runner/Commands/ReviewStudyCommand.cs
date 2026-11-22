using System.Globalization;
using System.Text.Json;

namespace Overens.Evals.Commands;

/// <summary>
/// The reviewer study: <c>--review-study [directory]</c> writes the page and its key, and
/// <c>--review-score directory answers.json</c> scores a reviewer's answers.
/// </summary>
internal static class ReviewStudyCommand
{
    public static async Task<int> RunAsync(Run run)
    {
        var directory = run.Option("--review-study", run.Option("--review-score", Path.Combine("TestResults", "review-study")));
        if (directory.StartsWith("--", StringComparison.Ordinal))
        {
            directory = Path.Combine("TestResults", "review-study");
        }

        if (run.Args.Contains("--review-score"))
        {
            var at = Array.IndexOf(run.Args, "--review-score");
            if (at + 2 >= run.Args.Length)
            {
                Console.Error.WriteLine("--review-score needs the study's directory and the answers file.");
                return 2;
            }

            var key = JsonSerializer.Deserialize<List<ReviewKey>>(await File.ReadAllTextAsync(Path.Combine(directory, "key.json")), EvalResults.Json)!;
            var score = ReviewStudy.Score(key, await File.ReadAllTextAsync(run.Args[at + 2]));
            Console.WriteLine($"Reviewer: {(score.Reviewer.Length > 0 ? score.Reviewer : "(no name)")}");
            Console.WriteLine($"Wrong rules rejected: {Interval.Of(score.Caught, score.Wrong)}");
            Console.WriteLine($"Right rules rejected: {score.RejectedRight} of {score.Right}");
            Console.WriteLine($"Cards left undecided: {score.Unanswered}");
            foreach (var missed in key.Where(card => score.Missed.Contains(card.Card)))
            {
                Console.WriteLine($"  missed card {missed.Card,2} ({missed.Agreement}): {missed.What}");
            }

            return 0;
        }

        var seed = int.Parse(run.Option("--seed", "2026"), CultureInfo.InvariantCulture);
        var (cards, written) = await ReviewStudy.BuildAsync(seed: seed);
        await ReviewStudy.WriteAsync(directory, cards, written);
        Console.WriteLine($"Wrote {cards.Count} cards, {written.Count(card => card.Wrong)} of them wrong, to {Path.Combine(directory, "review.html")}.");
        Console.WriteLine($"Give the reviewer the page only. The key is {Path.Combine(directory, "key.json")}.");
        return 0;
    }
}

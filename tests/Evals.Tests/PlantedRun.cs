using Overens.Gateway;
using Overens.Ingestion.Extraction;
using Overens.Ingestion.Reading;
using Overens.Synthetic;

namespace Overens.Evals.Tests;

/// <summary>
/// The planted-mistake study, run once for every test that needs it. It is the slowest thing
/// the evals do, and two test classes read it.
/// </summary>
internal static class PlantedRun
{
    public static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Overens.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    public static Lazy<Task<(PlantedResults Results, IReadOnlyList<(AgreementSpec Spec, ReadAgreement Read, ProposedRule Rule, string Mistake)> Silent)>> Sonnet { get; } = new(async () =>
    {
        using var recorded = new ReplayChatClient(null, Path.Combine(Root(), "evals", "recordings", ExtractionPrompt.Version, Models.Sonnet));
        return await PlantedMistakes.RunWithSilentAsync(recorded, Models.Sonnet);
    });
}

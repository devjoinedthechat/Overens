using Overens.Rules;

namespace Overens.Synthetic;

/// <summary>
/// The fixed set of test material every eval runs on. It is generated, never edited by hand,
/// and a test checks that the files in the repository are what the generator produces.
/// </summary>
public static class Corpus
{
    /// <summary>Three agreements of rising difficulty.</summary>
    public static IReadOnlyList<AgreementSpec> Agreements { get; } =
    [
        new("easy-2026", Seed: 101, Difficulty: 1, StartYear: 2026),
        new("medium-2026", Seed: 202, Difficulty: 2, StartYear: 2026),
        new("hard-2026", Seed: 303, Difficulty: 3, StartYear: 2026),
    ];

    /// <summary>Each agreement as a PDF, by its path under the corpus directory.</summary>
    public static IReadOnlyDictionary<string, byte[]> Pdfs() =>
        Agreements.ToDictionary(
            spec => $"{spec.Id}/agreement.pdf",
            spec => AgreementPdf.Render(AgreementGenerator.Generate(spec).Document));

    /// <summary>Every text file of the corpus, by its path under the corpus directory.</summary>
    public static IReadOnlyDictionary<string, string> Files()
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var spec in Agreements)
        {
            var agreement = AgreementGenerator.Generate(spec);
            // Pay data starts three months before the second year's rates, so it crosses a step.
            var pay = PayDataGenerator.Generate(agreement.Truth, spec.Seed, new(spec.StartYear, 12, 1), months: 6, employees: 12);
            files[$"{spec.Id}/agreement.json"] = RuleJson.Write(agreement.Document) + "\n";
            files[$"{spec.Id}/agreement.txt"] = AgreementText.Render(agreement.Document).ReplaceLineEndings("\n");
            files[$"{spec.Id}/rules.json"] = RuleJson.Write(agreement.Truth) + "\n";
            files[$"{spec.Id}/pay.json"] = RuleJson.Write(pay.Cases) + "\n";
            files[$"{spec.Id}/pay-answers.json"] = RuleJson.Write(pay.Errors) + "\n";
        }

        return files;
    }
}

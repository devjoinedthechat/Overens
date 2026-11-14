using Overens.Documents;
using Overens.Rules;

namespace Overens.Synthetic;

/// <summary>
/// The fixed set of test material every eval runs on. It is generated, never edited by hand,
/// and a test checks that the files in the repository are what the generator produces.
/// </summary>
public static class Corpus
{
    /// <summary>Three agreements of rising difficulty, and the renewal of the hardest.</summary>
    public static IReadOnlyList<AgreementSpec> Agreements { get; } =
    [
        new("easy-2026", Seed: 101, Difficulty: 1, StartYear: 2026),
        new("medium-2026", Seed: 202, Difficulty: 2, StartYear: 2026),
        new("hard-2026", Seed: 303, Difficulty: 3, StartYear: 2026),
        new("hard-2029", Seed: 404, Difficulty: 3, StartYear: 2029),
    ];

    /// <summary>
    /// Agreements written in ways the extraction prompt was not written against. They were
    /// added after the prompt was fixed, to find where extraction breaks.
    /// </summary>
    public static IReadOnlyList<AgreementSpec> Challenge { get; } =
    [
        new("twist-increases", Seed: 511, Difficulty: 2, StartYear: 2026, Twists.Increases),
        new("twist-dates", Seed: 512, Difficulty: 2, StartYear: 2026, Twists.NumericDates),
        new("twist-night", Seed: 513, Difficulty: 2, StartYear: 2026, Twists.SplitNight),
        new("twist-old-rates", Seed: 514, Difficulty: 3, StartYear: 2026, Twists.OldRates | Twists.TwoYearBand),
        new("twist-mid-year", Seed: 515, Difficulty: 3, StartYear: 2026, Twists.MidYearChange),
        new("twist-all", Seed: 516, Difficulty: 3, StartYear: 2026,
            Twists.Increases | Twists.NumericDates | Twists.SplitNight | Twists.OldRates | Twists.TwoYearBand | Twists.MidYearChange),
    ];

    /// <summary>Agreements that work against the reader: planted instructions, and a contradiction.</summary>
    public static IReadOnlyList<AgreementSpec> Attacks { get; } =
    [
        new("attack-planted", Seed: 611, Difficulty: 3, StartYear: 2026, Twists.Planted),
        new("attack-contradiction", Seed: 612, Difficulty: 2, StartYear: 2026, Twists.Contradiction),
    ];

    /// <summary>The agreement for a spec. <c>hard-2029</c> renews <c>hard-2026</c>.</summary>
    public static SyntheticAgreement Generate(AgreementSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Id == "hard-2029"
            ? AgreementGenerator.Renew(AgreementGenerator.Generate(Agreements[2]), spec)
            : AgreementGenerator.Generate(spec);
    }

    /// <summary>Each agreement as a PDF, by its path under the corpus directory.</summary>
    public static IReadOnlyDictionary<string, byte[]> Pdfs() =>
        Agreements.Concat(Challenge).Concat(Attacks).ToDictionary(spec => $"{spec.Id}/agreement.pdf", spec => AgreementPdf.Render(Generate(spec).Document));

    /// <summary>
    /// Six months of pay for twelve employees under an agreement. It starts three months
    /// before the second year's rates, so it crosses a step.
    /// </summary>
    public static PayData Pay(AgreementSpec spec, SyntheticAgreement agreement)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(agreement);
        return PayDataGenerator.Generate(agreement.Truth, spec.Seed, new(spec.StartYear, 12, 1), months: 6, employees: 12);
    }

    /// <summary>Every text file of the corpus, by its path under the corpus directory.</summary>
    public static IReadOnlyDictionary<string, string> Files()
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var spec in Agreements.Concat(Challenge).Concat(Attacks))
        {
            var agreement = Generate(spec);
            var pay = Pay(spec, agreement);
            files[$"{spec.Id}/agreement.json"] = RuleJson.Write(agreement.Document) + "\n";
            files[$"{spec.Id}/agreement.txt"] = AgreementText.Render(agreement.Document).ReplaceLineEndings("\n");
            files[$"{spec.Id}/rules.json"] = RuleJson.Write(agreement.Truth) + "\n";
            files[$"{spec.Id}/pay.json"] = RuleJson.Write(pay.Cases) + "\n";
            files[$"{spec.Id}/pay-answers.json"] = RuleJson.Write(pay.Errors) + "\n";
        }

        return files;
    }
}

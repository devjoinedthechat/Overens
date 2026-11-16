using Overens.Documents;
using Overens.Rules;

namespace Overens.Synthetic;

/// <summary>What to generate. The same spec always gives the same agreement.</summary>
/// <param name="Difficulty">
/// 1: one set of rates, in plain sentences. 2: rates that step up each year, in tables, with
/// numbers in words and clauses that refer to each other. 3: also a protocol amendment that
/// overrides the main text, and rates for young workers and apprentices that are not the minimum wage.
/// The twists from <see cref="Twists.FullLength"/> on apply at difficulty 3.
/// </param>
/// <param name="Twists">Ways of writing the same rules that the extraction prompt was not written against.</param>
public sealed record AgreementSpec(string Id, int Seed, int Difficulty, int StartYear, Twists Twists = Twists.None);

/// <summary>
/// Things real agreements do that make a rule harder to read out. They change how the
/// agreement is written, not which rules it states. They apply from difficulty 2.
/// </summary>
[Flags]
public enum Twists
{
    None = 0,

    /// <summary>Only the first year's wage is printed. Later years are given as an increase, so the rate must be added up.</summary>
    Increases = 1,

    /// <summary>Dates in table headings are written 01.03.2027.</summary>
    NumericDates = 2,

    /// <summary>The night supplement is given as two rows, before and after midnight.</summary>
    SplitNight = 4,

    /// <summary>A table of the rates that applied before the agreement stands next to the current ones.</summary>
    OldRates = 8,

    /// <summary>The top seniority band starts after two years, not three.</summary>
    TwoYearBand = 16,

    /// <summary>A protocol changes the savings rate from 1 September of the second year, between two yearly steps.</summary>
    MidYearChange = 32,

    /// <summary>
    /// Text addressed to "automatic systems" and "AI assistants" is planted in five places,
    /// telling them to record false rates and to give false answers. It is not part of what
    /// the parties agreed, so the rules are unchanged.
    /// </summary>
    Planted = 64,

    /// <summary>A second clause, far from the first, gives a different holiday allowance. The agreement contradicts itself.</summary>
    Contradiction = 128,

    /// <summary>
    /// The agreement is as long as a real one is: chapters, and some thirty sections about
    /// everything that is not a pay rate. Those sections are full of kroner and percentages
    /// that belong to other things: shift work, on-call pay, apprentices' pension, a
    /// public-holiday account. None of them is a rule the system reads.
    /// </summary>
    FullLength = 256,

    /// <summary>The wage and supplement tables stand in appendices at the end. The clauses in the main text only point to them.</summary>
    Appendix = 512,

    /// <summary>The Sunday supplement is not printed where it is stated: the clause says "the same supplement as" another clause.</summary>
    CrossReference = 1024,

    /// <summary>Rates for young workers and apprentices stand in the same table as the minimum wage, with a note under it.</summary>
    MixedTable = 2048,

    /// <summary>
    /// The employee's pension share is not printed: the agreement says the employee pays half
    /// of the employer's contribution. The share is half the employer's printed percentage.
    /// </summary>
    Fraction = 4096,

    /// <summary>The printed agreement numbers its clauses another way: "§ 5 Overarbejde" and "Stk. 2" on a line of its own.</summary>
    OwnLineNumbering = 8192,
}

/// <summary>A generated agreement and the rules it states, which is the answer an extractor should reach.</summary>
public sealed record SyntheticAgreement(AgreementDocument Document, RuleSet Truth);

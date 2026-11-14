using Microsoft.Extensions.AI;
using Overens.Agent.Questions;
using Overens.Documents;
using Overens.Rules;
using Overens.Synthetic;

namespace Overens.Evals;

/// <summary>A question with a known answer.</summary>
/// <param name="Kind">lookup, dated, calculation, text or unanswerable.</param>
/// <param name="MustSay">Each entry is a figure the answer must contain, in any of the ways it may be written.</param>
/// <param name="Clauses">The answer must cite one of these, when any are given.</param>
public sealed record Question(string Id, string Kind, string Text, bool Answerable, IReadOnlyList<IReadOnlyList<string>> MustSay, IReadOnlyList<string> Clauses);

/// <param name="Verdict">correct, wrong (shown and not right), abstained_wrongly, withheld or answered_unanswerable.</param>
public sealed record QuestionResult(string Agreement, string Id, string Kind, string Verdict, string Outcome, string Text, IReadOnlyList<string> Trace);

public static class Questions
{
    public static readonly DateOnly Today = new(2026, 11, 15);

    private static readonly string[] Agreements = ["easy-2026", "medium-2026", "hard-2026", "twist-all"];

    /// <summary>The questions for one agreement, written from its true rules, so every answer is known.</summary>
    public static IReadOnlyList<Question> For(SyntheticAgreement agreement)
    {
        ArgumentNullException.ThrowIfNull(agreement);
        var rules = agreement.Truth;
        var first = rules.Rules.Min(rule => rule.InForce.From);
        // A Tuesday in the first year and a day in the second, when most rates have stepped up.
        var early = Next(first.AddMonths(2), DayOfWeek.Tuesday);
        var later = first.AddYears(1).AddMonths(1).AddDays(14);
        var (now, then) = (rules.InForce(early), rules.InForce(later));
        string[] Sources<T>()
            where T : Rule => [.. rules.Rules.OfType<T>().Select(rule => rule.Source.ClauseId).Distinct()];

        decimal Line(PayLineKind line, Shift shift) =>
            PayEngine.Expect(rules, new(new("q", first.AddYears(-5)), shift.Date, shift.Date, [shift]), 200m)
                .Components.Where(part => part.Line == line).Sum(part => part.Amount);
        var overtime = Line(PayLineKind.Overtime, new(early, new(14, 0), new(23, 30)));
        var evening = Line(PayLineKind.InconvenientHours, new(early, new(16, 0), new(22, 0)));
        var notice = agreement.Document.Sections.Single(section => section.Heading == "Opsigelse").Clauses[0].Id;

        return
        [
            new("holiday", "lookup", "Hvor mange procent af lønnen udgør feriegodtgørelsen?", true,
                [Percent(now.HolidayAllowance!.Percent)], Sources<HolidayAllowanceRule>()),
            new("pension-employer", "dated", $"Hvor stor en procentdel af lønnen betaler arbejdsgiveren i pensionsbidrag den {Danish.Date(later)}?", true,
                [Percent(then.Pension!.EmployerPercent)], Sources<PensionRule>()),
            new("pension-wait", "lookup", "Hvor mange måneders anciennitet skal en medarbejder have for at være omfattet af pensionsordningen?", true,
                [[$"{then.Pension.QualifyingMonths} måned", $"{Danish.Word(then.Pension.QualifyingMonths)} måned"]], []),
            new("wage-new", "dated", $"Hvad er mindstebetalingen pr. time den {Danish.Date(later)} for en medarbejder, der lige er blevet ansat?", true,
                [Money(then.MinimumWage!.Bands[0].HourlyRate)], Sources<MinimumWageRule>()),
            new("wage-senior", "dated", $"Hvad er mindstebetalingen pr. time den {Danish.Date(early)} for en medarbejder med 14 måneders anciennitet?", true,
                [Money(now.MinimumWage!.RateFor(14))], Sources<MinimumWageRule>()),
            new("savings", "dated", $"Hvor mange procent af lønnen indbetaler arbejdsgiveren til fritvalgskontoen den {Danish.Date(later)}?", true,
                [Percent(then.SpecialSavings!.Percent)], Sources<SpecialSavingsRule>()),
            new("overtime-shift", "calculation",
                $"En medarbejder, der har været ansat i fem år og har en timeløn på 200 kr., arbejder {Danish.Date(early)} fra kl. 14.00 til kl. 23.30 uden pause. Hvor meget skal der betales i overarbejdstillæg for den vagt?", true,
                [Money(overtime)], Sources<OvertimeRule>()),
            new("evening-shift", "calculation",
                $"En medarbejder, der har været ansat i fem år og har en timeløn på 200 kr., arbejder {Danish.Date(early)} fra kl. 16.00 til kl. 22.00. Hvor meget skal der betales i tillæg for forskudt tid for den vagt?", true,
                [Money(evening)], Sources<InconvenientHoursRule>()),
            new("notice", "text", "Hvor langt er opsigelsesvarslet fra virksomhedens side efter prøvetiden for en medarbejder med under et års anciennitet?", true,
                [["21 dage"]], [notice]),
            new("parental-leave", "unanswerable", "Hvor mange ugers barselsorlov med fuld løn har en medarbejder ret til?", false, [], []),
            new("public-holidays", "unanswerable", "Hvad er tillægget pr. time for arbejde på helligdage som 1. juledag?", false, [], []),
            new("company-car", "unanswerable", "Har en medarbejder ret til firmabil efter overenskomsten?", false, [], []),
        ];

        static DateOnly Next(DateOnly from, DayOfWeek day) => from.AddDays(((int)day - (int)from.DayOfWeek + 7) % 7);
        static string[] Percent(decimal value) => [Danish.Percent(value).Replace(" pct.", "", StringComparison.Ordinal), value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',')];
        static string[] Money(decimal value) => [Danish.Money(value), Danish.Amount(value)];
    }

    /// <summary>Whether an answer is the known one.</summary>
    public static string Judge(Question question, QuestionAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(answer);
        if (!question.Answerable)
        {
            return answer.Outcome switch
            {
                AnswerOutcome.NotInAgreement => "correct",
                AnswerOutcome.Withheld => "withheld",
                _ => "answered_unanswerable",
            };
        }

        if (answer.Outcome != AnswerOutcome.Answered)
        {
            return answer.Outcome == AnswerOutcome.Withheld ? "withheld" : "abstained_wrongly";
        }

        var says = question.MustSay.All(ways => ways.Any(way => Says(answer.Text, way)));
        var cites = question.Clauses.Count == 0 || answer.Citations.Any(citation => question.Clauses.Contains(citation.ClauseId));
        return says && cites ? "correct" : "wrong";
    }

    /// <summary>
    /// Whether a text states a figure. A number is compared as a number, so 118 kr. states
    /// 118,00; anything else is looked for as written.
    /// </summary>
    private static bool Says(string text, string figure)
    {
        var wanted = Overens.Agent.FindingExplainer.Numbers(figure).ToList();
        return wanted.Count == 1 && figure.All(character => char.IsDigit(character) || character is ',' or '.')
            ? Overens.Agent.FindingExplainer.Numbers(text).Contains(wanted[0])
            : text.Contains(figure, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<IReadOnlyList<QuestionResult>> RunAsync(IChatClient model, string modelId, string only = "")
    {
        var agent = new QuestionAgent(model, modelId);
        var results = new List<QuestionResult>();
        foreach (var spec in Corpus.Agreements.Concat(Corpus.Challenge).Where(spec => Agreements.Contains(spec.Id) && (only.Length == 0 || spec.Id == only)))
        {
            var agreement = Corpus.Generate(spec);
            var index = new ClauseIndex(agreement.Document.Sections.Concat(agreement.Document.Protocols));
            foreach (var question in For(agreement))
            {
                var answer = await agent.AskAsync(question.Text, Today, agreement.Truth, index);
                results.Add(new(spec.Id, question.Id, question.Kind, Judge(question, answer), answer.Outcome.ToString(), answer.Text, answer.Trace));
            }
        }

        return results;
    }
}

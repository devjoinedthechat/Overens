using Overens.Documents;
using Overens.Rules;

namespace Overens.Synthetic.Tests;

public sealed class AgreementGeneratorTests
{
    public static TheoryData<int, int> SeedsAndDifficulties
    {
        get
        {
            var data = new TheoryData<int, int>();
            for (var seed = 1; seed <= 40; seed++)
            {
                data.Add(seed, (seed % 3) + 1);
            }

            return data;
        }
    }

    private static SyntheticAgreement Generate(int seed, int difficulty) =>
        AgreementGenerator.Generate(new($"agreement-{seed}", seed, difficulty, 2026));

    [Theory]
    [MemberData(nameof(SeedsAndDifficulties))]
    public void The_rules_it_states_are_a_usable_rule_set(int seed, int difficulty)
    {
        var agreement = Generate(seed, difficulty);

        Assert.Empty(RuleSetValidator.Validate(agreement.Truth));
        // Every kind of rule is in force on every day of the agreement's three years.
        foreach (var day in new[] { new DateOnly(2026, 3, 1), new DateOnly(2027, 2, 28), new DateOnly(2027, 3, 1), new DateOnly(2029, 2, 28) })
        {
            var rules = agreement.Truth.InForce(day);
            Assert.NotNull(rules.MinimumWage);
            Assert.NotNull(rules.Overtime);
            Assert.NotNull(rules.InconvenientHours);
            Assert.NotNull(rules.Pension);
            Assert.NotNull(rules.HolidayAllowance);
            Assert.NotNull(rules.SpecialSavings);
        }
    }

    [Theory]
    [MemberData(nameof(SeedsAndDifficulties))]
    public void Every_rule_quotes_words_that_stand_in_the_clause_it_names(int seed, int difficulty)
    {
        var agreement = Generate(seed, difficulty);
        var clauses = agreement.Document.Sections.Concat(agreement.Document.Protocols)
            .SelectMany(section => section.Clauses)
            .ToDictionary(clause => clause.Id, clause => AgreementText.Render(clause, 1));

        Assert.All(agreement.Truth.Rules, rule =>
        {
            Assert.True(clauses.TryGetValue(rule.Source.ClauseId, out var text), $"No clause {rule.Source.ClauseId}");
            Assert.Contains(rule.Source.Quote, text, StringComparison.Ordinal);
        });
    }

    [Theory]
    [MemberData(nameof(SeedsAndDifficulties))]
    public void Every_amount_in_the_rules_is_printed_in_the_agreement(int seed, int difficulty)
    {
        var agreement = Generate(seed, difficulty);
        var text = AgreementText.Render(agreement.Document);

        var amounts = agreement.Truth.Rules.SelectMany(rule => rule switch
        {
            MinimumWageRule wage => wage.Bands.Select(band => Danish(band.HourlyRate, 2)),
            InconvenientHoursRule hours => hours.Windows.Select(window => Danish(window.AmountPerHour, 2)),
            PensionRule pension => [Danish(pension.EmployerPercent, 1) + " pct.", Danish(pension.EmployeePercent, 1) + " pct."],
            HolidayAllowanceRule holiday => [Danish(holiday.Percent, 1) + " pct."],
            SpecialSavingsRule savings => [Danish(savings.Percent, 1) + " pct."],
            OvertimeRule overtime => overtime.Tiers.Select(tier => Danish(tier.Percent, 1) + " pct."),
            _ => [],
        });

        Assert.All(amounts, amount => Assert.Contains(amount, text, StringComparison.Ordinal));

        static string Danish(decimal value, int decimals) =>
            value.ToString(decimals == 2 ? "0.00" : "0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');
    }

    [Fact]
    public void The_same_spec_gives_the_same_agreement_and_another_seed_does_not()
    {
        Assert.Equal(RuleJson.Write(Generate(5, 3)), RuleJson.Write(Generate(5, 3)));
        Assert.NotEqual(RuleJson.Write(Generate(5, 3).Truth), RuleJson.Write(Generate(6, 3).Truth));
    }

    [Fact]
    public void Difficulty_adds_yearly_steps_tables_and_a_protocol_that_overrides_the_main_text()
    {
        var (easy, medium, hard) = (Generate(4, 1), Generate(4, 2), Generate(4, 3));

        Assert.DoesNotContain(Blocks(easy), block => block is TableBlock);
        Assert.Single(easy.Truth.Rules.OfType<MinimumWageRule>());
        Assert.Empty(easy.Document.Protocols);

        Assert.Contains(Blocks(medium), block => block is TableBlock);
        Assert.Equal(3, medium.Truth.Rules.OfType<MinimumWageRule>().Count());
        Assert.Single(medium.Truth.Rules.OfType<PensionRule>());

        // The protocol raises the employer's pension share from the second year.
        var pension = hard.Truth.Rules.OfType<PensionRule>().ToList();
        Assert.Equal(2, pension.Count);
        Assert.Equal("Protokollat 1, stk. 1", pension[1].Source.ClauseId);
        Assert.True(pension[1].EmployerPercent > pension[0].EmployerPercent);
        Assert.Equal(new DateOnly(2027, 3, 1), pension[0].InForce.Until);
        Assert.Contains("Lærlinge, 1. år", AgreementText.Render(hard.Document), StringComparison.Ordinal);

        static IEnumerable<Block> Blocks(SyntheticAgreement agreement) =>
            agreement.Document.Sections.SelectMany(section => section.Clauses).SelectMany(clause => clause.Blocks);
    }

    [Fact]
    public void A_renewal_keeps_the_parties_and_carries_the_rates_on_upwards()
    {
        var old = Generate(12, 3);
        var renewed = AgreementGenerator.Renew(old, new("renewed", 77, 3, 2029));

        Assert.Equal(old.Document.Parties, renewed.Document.Parties);
        Assert.Equal(old.Document.Title.Replace("2026-2029", "2029-2032", StringComparison.Ordinal), renewed.Document.Title);
        Assert.Empty(RuleSetValidator.Validate(renewed.Truth));
        Assert.All(renewed.Truth.Rules, rule => Assert.True(rule.InForce.From >= new DateOnly(2029, 3, 1)));

        // Every rate starts above where the old agreement ended; what was settled stays settled.
        Assert.True(First<MinimumWageRule>(renewed).Bands[0].HourlyRate > Last<MinimumWageRule>(old).Bands[0].HourlyRate);
        Assert.True(First<InconvenientHoursRule>(renewed).Windows[0].AmountPerHour > Last<InconvenientHoursRule>(old).Windows[0].AmountPerHour);
        Assert.True(First<SpecialSavingsRule>(renewed).Percent > Last<SpecialSavingsRule>(old).Percent);
        Assert.Equal(Last<PensionRule>(old).EmployerPercent, First<PensionRule>(renewed).EmployerPercent);
        Assert.Equal(Last<OvertimeRule>(old).Tiers, First<OvertimeRule>(renewed).Tiers);

        static T First<T>(SyntheticAgreement agreement)
            where T : Rule => agreement.Truth.Rules.OfType<T>().MinBy(rule => rule.InForce.From)!;
        static T Last<T>(SyntheticAgreement agreement)
            where T : Rule => agreement.Truth.Rules.OfType<T>().MaxBy(rule => rule.InForce.From)!;
    }

    [Fact]
    public void An_agreement_survives_being_written_and_read_and_its_rules_can_pay_people()
    {
        var agreement = Generate(9, 3);

        var read = RuleJson.Read<SyntheticAgreement>(RuleJson.Write(agreement));
        var pay = PayDataGenerator.Generate(read.Truth, seed: 3, new(2026, 12, 1), months: 6, employees: 10);

        Assert.Equal(RuleJson.Write(agreement), RuleJson.Write(read));
        Assert.Equal(60, pay.Cases.Count);
        Assert.NotEmpty(pay.Errors);
    }
}

using CsCheck;

namespace Overens.Rules.Tests;

/// <summary>Properties that must hold for any timesheet, checked on thousands of random ones.</summary>
public sealed class PayEnginePropertyTests
{
    private static readonly Gen<Shift> AnyShift = Gen.Select(Gen.Int[0, 29], Gen.Int[0, 1439], Gen.Int[1, 900])
        .Select((day, start, length) =>
        {
            var from = new TimeOnly(start / 60, start % 60);
            return new Shift(Sample.Start.AddDays(day), from, from.AddMinutes(length));
        });

    private static readonly Gen<RuleSet> AnyAgreement = Gen.Bool.Select(renewed => renewed ? Sample.Renewed : Sample.Agreement);

    private static readonly Gen<Employee> AnyEmployee =
        Gen.Int[0, 800].Select(days => new Employee("emp-x", Sample.Start.AddDays(-days)));

    /// <summary>An hourly wage in whole øre, from below the lowest minimum to well above the highest.</summary>
    private static readonly Gen<decimal> AnyRate = Gen.Int[12000, 30000].Select(oere => oere / 100m);

    private static readonly Gen<(RuleSet Rules, Timesheet Timesheet, decimal Rate)> AnyCase =
        Gen.Select(AnyAgreement, AnyEmployee, AnyShift.Array[0, 12], AnyRate)
            .Select((rules, employee, shifts, rate) => (rules, Sample.Sheet(employee, shifts), rate));

    [Fact]
    public void A_payslip_that_pays_what_is_expected_has_no_line_findings()
    {
        AnyCase.Sample(test =>
        {
            var payslip = Sample.Faithful(test.Rules, test.Timesheet, test.Rate);

            var findings = PayEngine.Check(test.Rules, test.Timesheet, payslip).Findings;

            Assert.DoesNotContain(findings, finding => finding.Line is not null);
        });
    }

    [Fact]
    public void Any_change_to_one_line_is_found_on_that_line_and_only_there()
    {
        Gen.Select(AnyCase, Gen.Enum<PayLineKind>(), Gen.Int[-50000, 50000].Where(oere => oere != 0))
            .Sample((test, line, oere) =>
            {
                var faithful = Sample.Faithful(test.Rules, test.Timesheet, test.Rate);
                var before = faithful.Lines.Where(paid => paid.Kind == line).Sum(paid => paid.Amount);
                var changed = faithful.With(line, before + (oere / 100m));

                var findings = PayEngine.Check(test.Rules, test.Timesheet, changed).Findings;

                var finding = Assert.Single(findings, finding => finding.Line is not null);
                Assert.Equal((line, oere / 100m), (finding.Line, finding.Difference));
            });
    }

    [Fact]
    public void Splitting_a_shift_in_two_changes_nothing()
    {
        // The second half must start on the same day, since a shift is paid by the day it starts.
        var sameDayShift = Gen.Select(Gen.Int[0, 29], Gen.Int[0, 600], Gen.Int[2, 839])
            .Select((day, start, length) =>
            {
                var from = new TimeOnly(start / 60, start % 60);
                return new Shift(Sample.Start.AddDays(day), from, from.AddMinutes(length));
            });

        Gen.Select(AnyCase, sameDayShift, Gen.Int[1, 838]).Sample((test, shift, cut) =>
        {
            var at = shift.Start.AddMinutes(Math.Min(cut, shift.Minutes - 1));
            var whole = test.Timesheet with { Shifts = [.. test.Timesheet.Shifts, shift] };
            var split = test.Timesheet with
            {
                Shifts = [.. test.Timesheet.Shifts, shift with { End = at }, shift with { Start = at }],
            };

            Assert.Equal(
                PayEngine.Expect(test.Rules, whole, test.Rate).Components,
                PayEngine.Expect(test.Rules, split, test.Rate).Components);
        });
    }

    [Fact]
    public void More_work_never_lowers_any_line_and_every_amount_is_in_whole_oere()
    {
        Gen.Select(AnyCase, AnyShift).Sample((test, extra) =>
        {
            var before = PayEngine.Expect(test.Rules, test.Timesheet, test.Rate).Components;
            var after = PayEngine.Expect(
                test.Rules,
                test.Timesheet with { Shifts = [.. test.Timesheet.Shifts, extra] },
                test.Rate).Components;

            Assert.All(after, part => Assert.Equal(decimal.Round(part.Amount, 2), part.Amount));
            foreach (var line in Enum.GetValues<PayLineKind>())
            {
                // Percentage lines are rounded per regime, so they may fall short by an øre each.
                var slack = line >= PayLineKind.PensionEmployer ? 0.02m : 0m;
                Assert.True(
                    Sum(after, line) >= Sum(before, line) - slack,
                    $"{line} fell from {Sum(before, line)} to {Sum(after, line)}");
            }
        });

        static decimal Sum(IReadOnlyList<Component> parts, PayLineKind line) =>
            parts.Where(part => part.Line == line).Sum(part => part.Amount);
    }
}

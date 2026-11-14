using Overens.Rules;
using Overens.Rules.Tests;

namespace Overens.Synthetic.Tests;

public sealed class PayDataGeneratorTests
{
    private static readonly PayData Data = PayDataGenerator.Generate(Sample.Renewed, seed: 7, new(2026, 3, 1), months: 6, employees: 25);

    [Fact]
    public void The_same_seed_gives_the_same_data_and_another_seed_does_not()
    {
        var again = PayDataGenerator.Generate(Sample.Renewed, seed: 7, new(2026, 3, 1), months: 6, employees: 25);
        var other = PayDataGenerator.Generate(Sample.Renewed, seed: 8, new(2026, 3, 1), months: 6, employees: 25);

        Assert.Equal(RuleJson.Write(Data), RuleJson.Write(again));
        Assert.NotEqual(RuleJson.Write(Data), RuleJson.Write(other));
    }

    [Fact]
    public void The_engine_finds_exactly_the_seeded_errors()
    {
        Assert.Equal(150, Data.Cases.Count);
        foreach (var (timesheet, payslip) in Data.Cases)
        {
            var seeded = Data.Errors.SingleOrDefault(error =>
                error.EmployeeId == timesheet.Employee.Id && error.PeriodStart == timesheet.PeriodStart);

            var findings = PayEngine.Check(Sample.Renewed, timesheet, payslip).Findings;

            Assert.Equal(
                seeded?.Lines ?? [],
                findings.Where(finding => finding.Line is not null)
                    .Select(finding => new WrongLine(finding.Line!.Value, finding.Expected, finding.Actual)));
            Assert.Equal(
                seeded?.RateBelowMinimum ?? false,
                findings.Any(finding => finding.Kind == FindingKind.RateBelowMinimum));
        }
    }

    [Fact]
    public void The_data_holds_every_kind_of_mistake_and_mostly_correct_payslips()
    {
        var large = PayDataGenerator.Generate(Sample.Renewed, seed: 11, new(2026, 3, 1), months: 12, employees: 40);

        Assert.Equal(PayDataGenerator.Mistakes.Order(), large.Errors.Select(error => error.Mistake).Distinct().Order());
        Assert.InRange(large.Errors.Count, large.Cases.Count * 2 / 10, large.Cases.Count * 4 / 10);
    }

    [Fact]
    public void Pay_data_survives_being_written_and_read()
    {
        var read = RuleJson.Read<PayData>(RuleJson.Write(Data));

        Assert.Equal(RuleJson.Write(Data), RuleJson.Write(read));
        Assert.Equal(Data.Cases[0].Timesheet.Shifts, read.Cases[0].Timesheet.Shifts);
    }

    [Fact]
    public void A_rule_set_survives_being_written_and_read()
    {
        var json = RuleJson.Write(Sample.Renewed);
        var read = RuleJson.Read<RuleSet>(json);

        Assert.Equal(json, RuleJson.Write(read));
        Assert.IsType<OvertimeRule>(read.Rules[1]);
        Assert.Contains("\"kind\": \"minimum_wage\"", json, StringComparison.Ordinal);
        Assert.Contains("§ 4, stk. 1", json, StringComparison.Ordinal);

        // Stored as jsonb, a rule comes back with "id" and "inForce" ahead of "kind".
        var reordered = """{"id": "holiday", "inForce": {"from": "2026-03-01", "until": null}, "kind": "holiday_allowance", "percent": 12.5, "source": {"quote": "q", "clauseId": "§ 1"}}""";
        Assert.Equal(12.5m, Assert.IsType<HolidayAllowanceRule>(RuleJson.Read<Rule>(reordered)).Percent);
    }
}

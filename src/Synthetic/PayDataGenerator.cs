using Overens.Rules;

namespace Overens.Synthetic;

/// <summary>
/// Makes months of timesheets and payslips for a rule set. Most payslips are right. The rest
/// carry a mistake a payroll could really make, such as last year's rates or a supplement
/// left out, and the answer is written down beside them.
///
/// The same seed always gives the same data.
/// </summary>
public static class PayDataGenerator
{
    /// <summary>The mistakes a payroll can make here, by name.</summary>
    public static IReadOnlyList<string> Mistakes { get; } =
    [
        "no_inconvenient_hours_supplement",
        "all_overtime_at_first_tier",
        "sunday_paid_as_weekday",
        "pension_without_qualifying_period",
        "pension_qualifying_period_too_long",
        "holiday_allowance_on_base_pay_only",
        "special_savings_left_out",
        "minimum_wage_not_applied",
        "line_mistyped",
    ];

    public static PayData Generate(RuleSet ruleSet, int seed, DateOnly firstMonth, int months, int employees)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        var random = new Random(seed);
        var staff = Enumerable.Range(1, employees).Select(number => Hire(ruleSet, random, number, firstMonth)).ToList();

        var cases = new List<PayCase>();
        var errors = new List<SeededError>();
        for (var month = 0; month < months; month++)
        {
            var start = new DateOnly(firstMonth.Year, firstMonth.Month, 1).AddMonths(month);
            var end = start.AddMonths(1).AddDays(-1);
            foreach (var (employee, rate, pattern) in staff)
            {
                var timesheet = new Timesheet(employee, start, end, Shifts(random, pattern, start, end));
                var payslip = Pay(ruleSet, timesheet, rate, rate);
                string? mistake = null;

                // Three payslips in ten carry a mistake: the first, in a random order, that
                // changes anything for this employee this month.
                if (random.Next(10) < 3)
                {
                    foreach (var candidate in Mistakes.OrderBy(_ => random.Next()).ToList())
                    {
                        var mistaken = Mistaken(candidate, ruleSet, timesheet, rate, payslip, random);
                        if (RuleJson.Write(mistaken) != RuleJson.Write(payslip))
                        {
                            (mistake, payslip) = (candidate, mistaken);
                            break;
                        }
                    }
                }

                // What the payslip should have said, given the wage printed on it.
                var right = Pay(ruleSet, timesheet, payslip.HourlyRate, payslip.HourlyRate);
                var wrong = Enum.GetValues<PayLineKind>()
                    .Select(line => new WrongLine(line, Total(right, line), Total(payslip, line)))
                    .Where(line => line.Expected != line.Actual)
                    .ToList();
                var belowMinimum = PayEngine.Expect(ruleSet, timesheet, payslip.HourlyRate).Shortfalls.Count > 0;
                cases.Add(new(timesheet, payslip));
                if (wrong.Count > 0 || belowMinimum)
                {
                    errors.Add(new(employee.Id, start, mistake ?? "wage_below_minimum", belowMinimum, wrong));
                }
            }
        }

        return new(cases, errors);
    }

    private static (Employee Employee, decimal Rate, Pattern Pattern) Hire(RuleSet ruleSet, Random random, int number, DateOnly firstMonth)
    {
        // Some are hired just before the data starts, so qualifying periods and seniority
        // steps fall inside it.
        // The minus stands outside the brackets on purpose. Without them it applied to the number
        // being switched on, every case but the first fell through to the last, and the days
        // were added: everyone was hired after the data began, with no seniority and no pension.
        var daysBefore = random.Next(0, 3) switch
        {
            0 => random.Next(1, 75),
            1 => random.Next(300, 400),
            _ => random.Next(400, 3000),
        };
        var hired = firstMonth.AddDays(-daysBefore);
        var employee = new Employee($"emp-{number:000}", hired);

        // A wage at or above the highest minimum the data will ever require of this employee,
        // in steps of 25 øre.
        var top = ruleSet.Rules.OfType<MinimumWageRule>().SelectMany(rule => rule.Bands).Max(band => (decimal?)band.HourlyRate) ?? 140m;
        var rate = top + (random.Next(0, 160) * 0.25m);
        return (employee, rate, (Pattern)random.Next(0, 4));
    }

    private static List<Shift> Shifts(Random random, Pattern pattern, DateOnly start, DateOnly end)
    {
        var shifts = new List<Shift>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            if (weekend && !(pattern == Pattern.Weekends && random.Next(3) == 0))
            {
                continue;
            }

            // One day in twelve is a day off.
            if (random.Next(12) == 0)
            {
                continue;
            }

            switch (pattern)
            {
                case Pattern.Evening:
                    shifts.Add(new(day, new(14, 0), new(22, random.Next(2) * 30)));
                    break;
                case Pattern.Night:
                    shifts.Add(new(day, new(22, 0), new(6, 0)));
                    break;
                default:
                    // A day shift with an unpaid lunch break, sometimes running late.
                    var late = random.Next(5) == 0 ? random.Next(1, 7) * 30 : 0;
                    shifts.Add(new(day, new(7, 0), new(11, 30)));
                    shifts.Add(new(day, new(12, 0), new TimeOnly(15, 0).AddMinutes(late)));
                    break;
            }
        }

        return shifts;
    }

    /// <summary>The payslip a payroll using <paramref name="ruleSet"/> would produce.</summary>
    private static Payslip Pay(RuleSet ruleSet, Timesheet timesheet, decimal payrollRate, decimal printedRate)
    {
        var lines = PayEngine.Expect(ruleSet, timesheet, payrollRate).Components
            .GroupBy(part => part.Line)
            .Select(line => new PayLine(line.Key, line.Sum(part => part.Amount)))
            .ToList();
        return new(timesheet.Employee.Id, timesheet.PeriodStart, timesheet.PeriodEnd, printedRate, lines);
    }

    private static Payslip Mistaken(string mistake, RuleSet ruleSet, Timesheet timesheet, decimal rate, Payslip right, Random random)
    {
        RuleSet With(Func<Rule, Rule?> change) =>
            ruleSet with { Rules = [.. ruleSet.Rules.Select(change).OfType<Rule>()] };

        switch (mistake)
        {
            case "no_inconvenient_hours_supplement":
                return Pay(With(rule => rule is InconvenientHoursRule ? null : rule), timesheet, rate, rate);
            case "all_overtime_at_first_tier":
                return Pay(With(rule => rule is OvertimeRule overtime ? overtime with { Tiers = [new(null, overtime.Tiers[0].Percent)] } : rule), timesheet, rate, rate);
            case "sunday_paid_as_weekday":
                return Pay(With(rule => rule is OvertimeRule overtime ? overtime with { SundayPercent = 0 } : rule), timesheet, rate, rate);
            case "pension_without_qualifying_period":
                return Pay(With(rule => rule is PensionRule pension ? pension with { QualifyingMonths = 0 } : rule), timesheet, rate, rate);
            case "pension_qualifying_period_too_long":
                return Pay(With(rule => rule is PensionRule pension ? pension with { QualifyingMonths = pension.QualifyingMonths + 6 } : rule), timesheet, rate, rate);
            case "special_savings_left_out":
                return right with { Lines = [.. right.Lines.Where(line => line.Kind != PayLineKind.SpecialSavings)] };
            case "holiday_allowance_on_base_pay_only":
                var percent = ruleSet.InForce(timesheet.PeriodStart).HolidayAllowance?.Percent ?? 0;
                var onBase = decimal.Round(Total(right, PayLineKind.BasePay) * percent / 100, 2, MidpointRounding.AwayFromZero);
                return Replace(right, PayLineKind.HolidayAllowance, onBase);
            case "minimum_wage_not_applied":
                // The employee is paid, and the payslip prints, a wage below the lowest minimum.
                var lowest = ruleSet.Rules.OfType<MinimumWageRule>().SelectMany(rule => rule.Bands).Min(band => (decimal?)band.HourlyRate) ?? rate;
                var low = lowest - (random.Next(1, 40) * 0.25m);
                return Pay(With(rule => rule is MinimumWageRule ? null : rule), timesheet, low, low);
            default:
                // Two neighbouring digits of one amount swapped.
                var line = right.Lines[random.Next(right.Lines.Count)];
                var digits = decimal.Truncate(line.Amount * 100).ToString(System.Globalization.CultureInfo.InvariantCulture).ToCharArray();
                var at = random.Next(Math.Max(1, digits.Length - 1));
                if (digits.Length > 1)
                {
                    (digits[at], digits[at + 1]) = (digits[at + 1], digits[at]);
                }

                var swapped = decimal.Parse(new string(digits), System.Globalization.CultureInfo.InvariantCulture) / 100;
                return Replace(right, line.Kind, swapped);
        }
    }

    private static Payslip Replace(Payslip payslip, PayLineKind kind, decimal amount) =>
        payslip with { Lines = [.. payslip.Lines.Select(line => line.Kind == kind ? line with { Amount = amount } : line)] };

    private static decimal Total(Payslip payslip, PayLineKind line) =>
        payslip.Lines.Where(paid => paid.Kind == line).Sum(paid => paid.Amount);

    private enum Pattern
    {
        Day,
        Evening,
        Night,
        Weekends,
    }
}

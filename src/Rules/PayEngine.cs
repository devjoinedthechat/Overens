namespace Overens.Rules;

/// <summary>
/// Works out what a pay period should have paid under a rule set, and compares that with the
/// payslip. Everything here is arithmetic on <see cref="decimal"/>; no model takes part.
///
/// A shift is paid by the rules in force on the day it starts. Each component is rounded to
/// the øre, halves away from zero, and a line is the sum of its components.
/// </summary>
public static class PayEngine
{
    /// <summary>Above any real hourly wage, and far below where the arithmetic could overflow.</summary>
    public const decimal MaxHourlyRate = 100_000m;

    public static PayCheck Check(RuleSet ruleSet, Timesheet timesheet, Payslip payslip)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(timesheet);
        ArgumentNullException.ThrowIfNull(payslip);
        if (payslip.EmployeeId != timesheet.Employee.Id
            || payslip.PeriodStart != timesheet.PeriodStart
            || payslip.PeriodEnd != timesheet.PeriodEnd)
        {
            throw new ArgumentException("The payslip and the timesheet are for different employees or periods.", nameof(payslip));
        }

        var expectation = Expect(ruleSet, timesheet, payslip.HourlyRate);
        return new PayCheck(expectation.Components, Compare(ruleSet, expectation, payslip));
    }

    /// <summary>What the period should pay an employee whose own hourly wage is <paramref name="hourlyRate"/>.</summary>
    public static Expectation Expect(RuleSet ruleSet, Timesheet timesheet, decimal hourlyRate)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(timesheet);
        if (hourlyRate is < 0 or > MaxHourlyRate)
        {
            throw new ArgumentException($"An hourly wage is between 0 and {MaxHourlyRate:0} kr.", nameof(hourlyRate));
        }

        DateTime? busyUntil = null;
        var minutes = new Dictionary<WageKey, int>();
        var worked = new Dictionary<DateOnly, DayCount>();
        var shortfalls = new Dictionary<MinimumWageRule, decimal>();

        foreach (var shift in timesheet.Shifts.OrderBy(shift => shift.Date).ThenBy(shift => shift.Start))
        {
            if (shift.Date < timesheet.PeriodStart || shift.Date > timesheet.PeriodEnd || shift.Minutes == 0)
            {
                throw new ArgumentException($"The shift on {shift.Date:yyyy-MM-dd} is outside the period or has no length.", nameof(timesheet));
            }

            // The same time worked twice would be paid twice, and a correct payslip would look too low.
            var begins = shift.Date.ToDateTime(shift.Start);
            if (begins < busyUntil)
            {
                throw new ArgumentException($"The shift on {shift.Date:yyyy-MM-dd} at {shift.Start:HH\\:mm} overlaps the shift before it.", nameof(timesheet));
            }

            busyUntil = begins.AddMinutes(shift.Minutes);

            var rules = ruleSet.InForce(shift.Date);
            var months = timesheet.Employee.MonthsEmployedOn(shift.Date);

            // An employee paid below the minimum is owed the minimum.
            var rate = hourlyRate;
            MinimumWageRule? raisedBy = null;
            if (rules.MinimumWage is { } wage && wage.RateFor(months) > hourlyRate)
            {
                rate = wage.RateFor(months);
                raisedBy = wage;
                shortfalls[wage] = Math.Max(rate, shortfalls.GetValueOrDefault(wage));
            }

            var regime = new Regime(
                rules.Pension is { } pension && months >= pension.QualifyingMonths ? pension : null,
                rules.HolidayAllowance,
                rules.SpecialSavings);
            var day = worked.GetValueOrDefault(shift.Date);
            var start = shift.Date.ToDateTime(shift.Start);

            // A shift touches a handful of rates, so its minutes are tallied in a short list
            // and added to the totals once.
            var tally = new List<(WageKey Key, int Minutes)>();
            void Add(WageKey key)
            {
                for (var index = 0; index < tally.Count; index++)
                {
                    var (held, count) = tally[index];
                    if (held.Line == key.Line && ReferenceEquals(held.Rule, key.Rule) && held.Rate == key.Rate)
                    {
                        tally[index] = (held, count + 1);
                        return;
                    }
                }

                tally.Add((key, 1));
            }

            for (var minute = 0; minute < shift.Minutes; minute++)
            {
                var moment = start.AddMinutes(minute);
                Add(new(regime, PayLineKind.BasePay, raisedBy, rate));

                if (rules.Overtime is { } overtime)
                {
                    var percent = 0m;
                    if (moment.DayOfWeek == DayOfWeek.Sunday)
                    {
                        percent = overtime.SundayPercent;
                    }
                    else if (day.Normal < overtime.DailyNormHours * 60)
                    {
                        day = day with { Normal = day.Normal + 1 };
                    }
                    else
                    {
                        percent = overtime.PercentAfter(day.Overtime);
                        day = day with { Overtime = day.Overtime + 1 };
                    }

                    if (percent > 0)
                    {
                        Add(new(regime, PayLineKind.Overtime, overtime, rate * percent / 100));
                    }
                }

                if (rules.InconvenientHours is { } inconvenient && inconvenient.AmountAt(moment) is > 0 and var amount)
                {
                    Add(new(regime, PayLineKind.InconvenientHours, inconvenient, amount));
                }
            }

            worked[shift.Date] = day;
            foreach (var (key, count) in tally)
            {
                minutes[key] = minutes.GetValueOrDefault(key) + count;
            }
        }

        // Minutes are counted first and multiplied once, so no rounding error builds up.
        var components = new List<Component>();
        var pay = new Dictionary<Regime, decimal>();
        foreach (var (key, count) in minutes)
        {
            var amount = ToOere(count * key.Rate / 60);
            components.Add(new(key.Line, key.Rule?.Id, count, null, key.Rate, amount));
            pay[key.Regime] = pay.GetValueOrDefault(key.Regime) + amount;
        }

        foreach (var (regime, basis) in pay)
        {
            void Share(PayLineKind line, Rule? rule, decimal percent)
            {
                if (rule is not null && percent > 0)
                {
                    components.Add(new(line, rule.Id, null, basis, percent, ToOere(basis * percent / 100)));
                }
            }

            Share(PayLineKind.PensionEmployer, regime.Pension, regime.Pension?.EmployerPercent ?? 0);
            Share(PayLineKind.PensionEmployee, regime.Pension, regime.Pension?.EmployeePercent ?? 0);
            Share(PayLineKind.HolidayAllowance, regime.HolidayAllowance, regime.HolidayAllowance?.Percent ?? 0);
            Share(PayLineKind.SpecialSavings, regime.SpecialSavings, regime.SpecialSavings?.Percent ?? 0);
        }

        return new Expectation(
            [.. components.OrderBy(part => part.Line).ThenBy(part => part.RuleId, StringComparer.Ordinal).ThenBy(part => part.Rate).ThenBy(part => part.Basis)],
            shortfalls);
    }

    private static List<Finding> Compare(RuleSet ruleSet, Expectation expectation, Payslip payslip)
    {
        var findings = new List<Finding>();
        foreach (var (rule, minimum) in expectation.Shortfalls.OrderBy(pair => pair.Key.InForce.From))
        {
            findings.Add(new(FindingKind.RateBelowMinimum, null, minimum, payslip.HourlyRate, [rule]));
        }

        var byId = ruleSet.Rules.ToDictionary(rule => rule.Id);
        foreach (var line in Enum.GetValues<PayLineKind>())
        {
            var parts = expectation.Components.Where(part => part.Line == line).ToList();
            var actual = payslip.Lines.Where(paid => paid.Kind == line).ToList();
            var expected = parts.Sum(part => part.Amount);
            var paid = actual.Sum(paid => paid.Amount);
            if (expected == paid)
            {
                continue;
            }

            var kind = actual.Count == 0 ? FindingKind.MissingLine
                : parts.Count == 0 ? FindingKind.UnexpectedLine
                : paid < expected ? FindingKind.TooLow
                : FindingKind.TooHigh;
            Rule[] rules = [.. parts.Select(part => part.RuleId).OfType<string>().Distinct().Select(id => byId[id])];
            findings.Add(new(kind, line, expected, paid, rules));
        }

        return findings;
    }

    private static decimal ToOere(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>The percentage rules a shift's pay falls under. Pay is totalled per regime.</summary>
    private sealed record Regime(PensionRule? Pension, HolidayAllowanceRule? HolidayAllowance, SpecialSavingsRule? SpecialSavings);

    private readonly record struct WageKey(Regime Regime, PayLineKind Line, Rule? Rule, decimal Rate);

    private readonly record struct DayCount(int Normal, int Overtime);
}

/// <summary>
/// What a period should pay. <see cref="Shortfalls"/> holds each minimum wage rule the
/// employee's own wage fell below, with the minimum it required.
/// </summary>
public sealed record Expectation(
    IReadOnlyList<Component> Components,
    IReadOnlyDictionary<MinimumWageRule, decimal> Shortfalls);

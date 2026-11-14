using System.Text.Json.Serialization;

namespace Overens.Rules;

/// <summary>Where a rule comes from: the clause, and the words in it that state the rule.</summary>
public sealed record Source(string ClauseId, string Quote);

/// <summary>One thing a collective agreement says about pay, for a span of days.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MinimumWageRule), "minimum_wage")]
[JsonDerivedType(typeof(OvertimeRule), "overtime")]
[JsonDerivedType(typeof(InconvenientHoursRule), "inconvenient_hours")]
[JsonDerivedType(typeof(PensionRule), "pension")]
[JsonDerivedType(typeof(HolidayAllowanceRule), "holiday_allowance")]
[JsonDerivedType(typeof(SpecialSavingsRule), "special_savings")]
public abstract record Rule(string Id, Period InForce, Source Source);

/// <summary>The lowest hourly wage, by how long the employee has been employed.</summary>
public sealed record MinimumWageRule(string Id, Period InForce, Source Source, IReadOnlyList<SeniorityBand> Bands)
    : Rule(Id, InForce, Source)
{
    /// <summary>The minimum for an employee with this many full months of employment.</summary>
    public decimal RateFor(int months) => Bands.Last(band => band.FromMonths <= months).HourlyRate;
}

/// <summary>A minimum hourly rate from a number of full months of employment onwards.</summary>
public sealed record SeniorityBand(int FromMonths, decimal HourlyRate);

/// <summary>
/// A supplement, as a percentage of the hourly wage, for hours beyond the daily norm and for
/// every hour worked on a Sunday.
/// </summary>
public sealed record OvertimeRule(
    string Id,
    Period InForce,
    Source Source,
    decimal DailyNormHours,
    IReadOnlyList<OvertimeTier> Tiers,
    decimal SundayPercent)
    : Rule(Id, InForce, Source)
{
    /// <summary>The supplement for a minute of overtime after this many minutes of it that day.</summary>
    public decimal PercentAfter(int overtimeMinutes)
    {
        var reached = 0m;
        foreach (var tier in Tiers)
        {
            if (tier.Hours is not { } hours)
            {
                return tier.Percent;
            }

            reached += hours * 60;
            if (overtimeMinutes < reached)
            {
                return tier.Percent;
            }
        }

        return Tiers[^1].Percent;
    }
}

/// <summary>
/// A stretch of a day's overtime and its supplement. <see cref="Hours"/> is how long the
/// stretch lasts; the last tier has none and covers the rest.
/// </summary>
public sealed record OvertimeTier(decimal? Hours, decimal Percent);

/// <summary>A fixed amount per hour for work at inconvenient times.</summary>
public sealed record InconvenientHoursRule(string Id, Period InForce, Source Source, IReadOnlyList<TimeWindow> Windows)
    : Rule(Id, InForce, Source)
{
    /// <summary>
    /// The supplement for the minute starting at this moment. Where windows overlap, the
    /// highest applies; supplements are not added together.
    /// </summary>
    public decimal AmountAt(DateTime moment)
    {
        var amount = 0m;
        foreach (var window in Windows)
        {
            if (window.Contains(moment) && window.AmountPerHour > amount)
            {
                amount = window.AmountPerHour;
            }
        }

        return amount;
    }
}

/// <summary>
/// Clock times on certain weekdays. When <see cref="To"/> is not after <see cref="From"/> the
/// window runs past midnight: 22:00 to 06:00 covers late evening and early morning of each day.
/// </summary>
public sealed record TimeWindow(IReadOnlyList<DayOfWeek> Days, TimeOnly From, TimeOnly To, decimal AmountPerHour)
{
    public bool Contains(DateTime moment)
    {
        var time = TimeOnly.FromDateTime(moment);
        var inHours = From < To ? time >= From && time < To : time >= From || time < To;
        return inHours && Days.Contains(moment.DayOfWeek);
    }
}

/// <summary>
/// Pension contributions as percentages of pay, once the employee has been employed for
/// <see cref="QualifyingMonths"/> full months.
/// </summary>
public sealed record PensionRule(
    string Id,
    Period InForce,
    Source Source,
    decimal EmployerPercent,
    decimal EmployeePercent,
    int QualifyingMonths)
    : Rule(Id, InForce, Source);

/// <summary>Holiday allowance as a percentage of pay.</summary>
public sealed record HolidayAllowanceRule(string Id, Period InForce, Source Source, decimal Percent)
    : Rule(Id, InForce, Source);

/// <summary>Special savings (fritvalg) as a percentage of pay.</summary>
public sealed record SpecialSavingsRule(string Id, Period InForce, Source Source, decimal Percent)
    : Rule(Id, InForce, Source);

namespace Overens.Rules;

/// <summary>
/// The days a rule applies. <see cref="Until"/> is the first day it no longer does,
/// or null when no end is known.
/// </summary>
public readonly record struct Period(DateOnly From, DateOnly? Until = null)
{
    public bool Contains(DateOnly day) => day >= From && (Until is null || day < Until);

    public bool Overlaps(Period other) =>
        (other.Until is null || From < other.Until) && (Until is null || other.From < Until);
}

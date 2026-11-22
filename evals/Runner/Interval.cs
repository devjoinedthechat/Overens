namespace Overens.Evals;

/// <summary>
/// How sure a share can be, given how few cases it rests on. 40 of 51 is a share somewhere
/// between about two thirds and nine tenths; 48 of 48 is not "always", only "at least 93 in
/// 100" at the same confidence.
/// </summary>
public static class Interval
{
    /// <summary>The 95 pct. Wilson score interval for <paramref name="successes"/> of <paramref name="cases"/>, as two percentages.</summary>
    public static (decimal Low, decimal High) Wilson(int successes, int cases)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(successes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(successes, cases);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cases);
        const double z = 1.959963984540054;
        double n = cases, p = successes / n;
        var centre = (p + (z * z / (2 * n))) / (1 + (z * z / n));
        var spread = z * Math.Sqrt((p * (1 - p) / n) + (z * z / (4 * n * n))) / (1 + (z * z / n));
        return ((decimal)Math.Round(Math.Max(0, centre - spread) * 100, MidpointRounding.AwayFromZero), (decimal)Math.Round(Math.Min(1, centre + spread) * 100, MidpointRounding.AwayFromZero));
    }

    /// <summary>"40 of 51 (65–88 pct.)", as the report prints it.</summary>
    public static string Of(int successes, int cases)
    {
        var (low, high) = Wilson(successes, cases);
        return $"{successes} of {cases} ({low}–{high} pct.)";
    }
}

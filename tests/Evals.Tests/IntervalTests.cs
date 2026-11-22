namespace Overens.Evals.Tests;

public sealed class IntervalTests
{
    [Theory]
    // Values from the closed form, checked by hand.
    [InlineData(40, 51, 65, 88)]
    [InlineData(30, 51, 45, 71)]
    [InlineData(48, 48, 93, 100)]
    [InlineData(200, 200, 98, 100)]
    [InlineData(0, 10, 0, 28)]
    [InlineData(5, 10, 24, 76)]
    public void The_interval_is_the_wilson_score_interval(int successes, int cases, int low, int high)
    {
        Assert.Equal((low, high), Interval.Wilson(successes, cases));
    }

    [Fact]
    public void A_share_of_no_cases_or_more_than_all_of_them_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Interval.Wilson(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Interval.Wilson(3, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Interval.Wilson(-1, 2));
    }
}

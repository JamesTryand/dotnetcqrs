using DotnetCqrs.Codegen.Generation;

namespace DotnetCqrs.Tests.Codegen;

/// <summary>
/// Pure date-math unit tests, independent of the generate/build/run round trips
/// elsewhere in this directory: <see cref="DateRangeResolver"/> takes "today" as a
/// parameter specifically so these can be fully deterministic rather than depending on
/// the real clock.
/// </summary>
public class DateRangeResolverTests
{
    private static readonly DateOnly Today = new(2026, 9, 2);

    [Fact]
    public void Last7Days_is_a_six_day_lookback_through_today_inclusive()
    {
        var (from, to) = DateRangeResolver.ResolveBounds("last7Days", from: null, to: null, Today);

        Assert.Equal("2026-08-27", from);
        Assert.Equal("2026-09-02", to);
    }

    [Fact]
    public void LastCalendarMonth_spans_the_whole_previous_month_regardless_of_todays_day()
    {
        var (from, to) = DateRangeResolver.ResolveBounds("lastCalendarMonth", from: null, to: null, Today);

        Assert.Equal("2026-08-01", from);
        Assert.Equal("2026-08-31", to);
    }

    [Fact]
    public void LastCalendarMonth_crosses_a_year_boundary_in_January()
    {
        var (from, to) = DateRangeResolver.ResolveBounds("lastCalendarMonth", from: null, to: null, new DateOnly(2027, 1, 15));

        Assert.Equal("2026-12-01", from);
        Assert.Equal("2026-12-31", to);
    }

    [Fact]
    public void Custom_uses_the_supplied_bounds_verbatim()
    {
        var (from, to) = DateRangeResolver.ResolveBounds("custom", "2026-08-01", "2026-08-31", Today);

        Assert.Equal("2026-08-01", from);
        Assert.Equal("2026-08-31", to);
    }

    [Fact]
    public void Custom_without_from_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DateRangeResolver.ResolveBounds("custom", null, "2026-08-31", Today));
        Assert.Contains("from", ex.Message);
    }

    [Fact]
    public void An_unknown_preset_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DateRangeResolver.ResolveBounds("lastQuarter", null, null, Today));
        Assert.Contains("lastQuarter", ex.Message);
    }
}

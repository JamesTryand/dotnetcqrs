namespace DotnetCqrs.Codegen.Generation;

/// <summary>
/// Resolves a <c>readModel.filters</c> dateRange param's runtime value (schema 2.4.0,
/// the documented <c>{ kind, from?, to? }</c> convention -- see
/// <c>eventmodelschema</c>'s own design notes) into concrete inclusive bounds, as ISO
/// 8601 date strings (<c>yyyy-MM-dd</c>) -- lexicographic string comparison against a
/// TEXT-affinity date column already sorts correctly, so no further conversion is
/// needed at the SQL layer.
///
/// Shared, not duplicated, between the real generated query route
/// (<see cref="ReadModelQueryGenerator"/>) and the scenario-verify harness's own
/// simulation of it (<c>Verification/HarnessProgram.txt</c>'s <c>SelectRowsAsync</c>) --
/// both reference this project directly (the harness's own scratch project takes a
/// <c>ProjectReference</c> to it, the same way <see cref="HostProjectGenerator"/>'s
/// generated host already does for its baked-in <c>--verify</c> mode). This is the
/// concrete answer to the risk this project's own Group C item 4 execution plan named:
/// <c>readModel.scopes</c> has zero production consumers today because the real query
/// path and the harness's simulation of it were never the same code, so they were
/// always free to drift; the actual date-preset arithmetic for <c>filters</c> living in
/// exactly one place is what keeps that from happening here.
/// </summary>
public static class DateRangeResolver
{
    /// <param name="today">The caller's own notion of "now", as a date -- injected
    /// rather than read from <see cref="DateTime.UtcNow"/> here, so a test can pin it
    /// and get a fully deterministic <c>last7Days</c>/<c>lastCalendarMonth</c> bound.</param>
    public static (string From, string To) ResolveBounds(string kind, string? from, string? to, DateOnly today) => kind switch
    {
        "last7Days" => (today.AddDays(-6).ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd")),
        "lastCalendarMonth" => LastCalendarMonthBounds(today),
        "custom" => (
            from ?? throw new InvalidOperationException("dateRange kind \"custom\" requires a \"from\" value"),
            to ?? throw new InvalidOperationException("dateRange kind \"custom\" requires a \"to\" value")),
        _ => throw new InvalidOperationException($"unknown dateRange preset \"{kind}\""),
    };

    private static (string, string) LastCalendarMonthBounds(DateOnly today)
    {
        var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);
        var lastOfPreviousMonth = firstOfThisMonth.AddDays(-1);
        var firstOfPreviousMonth = new DateOnly(lastOfPreviousMonth.Year, lastOfPreviousMonth.Month, 1);
        return (firstOfPreviousMonth.ToString("yyyy-MM-dd"), lastOfPreviousMonth.ToString("yyyy-MM-dd"));
    }
}

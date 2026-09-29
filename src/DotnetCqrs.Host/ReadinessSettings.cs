using System.Globalization;
using DotnetCqrs.Consumers;

namespace DotnetCqrs.Host;

/// <summary>
/// The readiness thresholds the health/telemetry contract leaves to each stack (section 4.8):
/// how far behind a read model may be and still count as current, and how long a writer may spend
/// on its initial catch-up before it serves anyway. Both are in seconds, decimals allowed; unset
/// means the default.
/// </summary>
public sealed record ReadinessSettings(TimeSpan LagThreshold, TimeSpan CatchUpDeadline)
{
    public const string LagThresholdVariable = "DOTNETCQRS_LAG_THRESHOLD_SECONDS";
    public const string CatchUpDeadlineVariable = "DOTNETCQRS_CATCHUP_DEADLINE_SECONDS";
    public const string HeartbeatIntervalVariable = "DOTNETCQRS_HEARTBEAT_INTERVAL_SECONDS";
    public const string StaleThresholdVariable = "DOTNETCQRS_STALE_THRESHOLD_SECONDS";

    public static readonly TimeSpan DefaultCatchUpDeadline = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultStaleThreshold = TimeSpan.FromSeconds(5);

    /// <summary>How often the writer upserts its heartbeat row (contract section 5, <i>h</i>), and
    /// how often a reader measures it. Keep it well under <see cref="StaleThreshold"/>.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = DefaultHeartbeatInterval;

    /// <summary>How old a reader's view of the heartbeat may be before it counts as stale.</summary>
    public TimeSpan StaleThreshold { get; init; } = DefaultStaleThreshold;

    /// <summary>Reads both from the environment. Throws <see cref="InvalidReadinessSettingException"/>
    /// for anything but a non-negative number, which fails the boot like any other bad setting.</summary>
    public static ReadinessSettings FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable(LagThresholdVariable),
        Environment.GetEnvironmentVariable(CatchUpDeadlineVariable),
        Environment.GetEnvironmentVariable(HeartbeatIntervalVariable),
        Environment.GetEnvironmentVariable(StaleThresholdVariable));

    public static ReadinessSettings Parse(
        string? lagThreshold, string? catchUpDeadline, string? heartbeatInterval = null, string? staleThreshold = null) =>
        new(Seconds(LagThresholdVariable, lagThreshold, ConsumerEngine.DefaultLagThreshold),
            Seconds(CatchUpDeadlineVariable, catchUpDeadline, DefaultCatchUpDeadline))
        {
            HeartbeatInterval = Seconds(HeartbeatIntervalVariable, heartbeatInterval, DefaultHeartbeatInterval),
            StaleThreshold = Seconds(StaleThresholdVariable, staleThreshold, DefaultStaleThreshold),
        };

    private static TimeSpan Seconds(string name, string? configured, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return fallback;
        return double.TryParse(configured, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
               && seconds <= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidReadinessSettingException(name, configured);
    }
}

/// <summary>An invalid readiness threshold: the boot fails, like any other invalid setting.</summary>
public sealed class InvalidReadinessSettingException(string name, string value) : Exception(
    $"{name} '{value}' is not a valid number of seconds: use a number such as 5 or 0.5.")
{
    public string Name { get; } = name;

    public string Value { get; } = value;
}

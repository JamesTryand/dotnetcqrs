using System.Globalization;

namespace DotnetCqrs.Host.Telemetry;

/// <summary>
/// Whether and how the node pushes telemetry (health/telemetry contract sections 8 and 9):
/// <c>CQRS_TELEMETRY_URL</c>, where the scheme selects the transport and unset means off, and
/// <c>CQRS_TELEMETRY_INTERVAL</c>, the seconds between snapshots (decimals allowed, default 15).
/// </summary>
public sealed record TelemetrySettings(Uri? Url, TimeSpan Interval)
{
    public const string UrlVariable = "CQRS_TELEMETRY_URL";
    public const string IntervalVariable = "CQRS_TELEMETRY_INTERVAL";
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    public bool Enabled => Url is not null;

    /// <summary>Reads both from the environment. Throws <see cref="InvalidTelemetrySettingException"/>
    /// for a value that is not a URL or not a positive number of seconds, which fails the boot like
    /// any other bad setting.</summary>
    public static TelemetrySettings FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(UrlVariable), Environment.GetEnvironmentVariable(IntervalVariable));

    public static TelemetrySettings Parse(string? url, string? interval)
    {
        Uri? parsed = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            // The URL may carry credentials, so a message names its scheme and host, never the whole of it.
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out parsed) || string.IsNullOrEmpty(parsed.Host))
                throw new InvalidTelemetrySettingException(UrlVariable,
                    "is not a URL: use the bus's address, such as nats://host:4222.");
        }
        var every = DefaultInterval;
        if (!string.IsNullOrWhiteSpace(interval))
        {
            if (!double.TryParse(interval, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
                || seconds <= 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
                throw new InvalidTelemetrySettingException(IntervalVariable,
                    $"'{interval}' is not a positive number of seconds: use a number such as 15 or 0.5.");
            every = TimeSpan.FromSeconds(seconds);
        }
        return new TelemetrySettings(parsed, every);
    }
}

/// <summary>An invalid telemetry setting: the boot fails, like any other invalid setting. An
/// unreachable bus is not one.</summary>
public sealed class InvalidTelemetrySettingException(string name, string problem)
    : Exception($"{name} {problem}")
{
    public string Name { get; } = name;
}

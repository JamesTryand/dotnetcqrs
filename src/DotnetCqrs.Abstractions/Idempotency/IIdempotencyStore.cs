namespace DotnetCqrs.Idempotency;

/// <summary>A recorded answer to a command sent with an <c>Idempotency-Key</c>: the hash of the request it answered
/// (aggregate, id, command and body) and the exact bytes sent back, so a replay is verbatim.</summary>
public sealed record IdempotencyRecord(string RequestHash, int StatusCode, string ContentType, byte[] Body);

/// <summary>
/// Where the command gateway keeps the answers it may replay (the command gateway contract v0.1, G7). Keyed by the
/// caller's key alone. A record is kept for the store's retention, at least <see cref="IdempotencyRetention.Minimum"/>,
/// and an expired one is never returned. Implementations: <c>InMemoryIdempotencyStore</c> (DotnetCqrs.Host, one
/// process, lost on restart), <c>SqliteIdempotencyStore</c> (DotnetCqrs) and <c>PostgresIdempotencyStore</c>
/// (DotnetCqrs.Postgres).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>The record kept under <paramref name="key"/>, or null when there is none or it has expired.</summary>
    Task<IdempotencyRecord?> FindAsync(string key, CancellationToken ct = default);

    /// <summary>Records <paramref name="record"/> under <paramref name="key"/> unless a live record is already there:
    /// the first write wins. Returns whether this one was kept. Also drops expired records.</summary>
    Task<bool> TryAddAsync(string key, IdempotencyRecord record, CancellationToken ct = default);
}

/// <summary>Thrown for a retention setting that is not a whole number of days, or is under the minimum.</summary>
public sealed class InvalidIdempotencySettingException(string message) : Exception(message);

/// <summary>How long a recorded answer is kept. A payment processor redelivers for days, so the contract's floor is 7
/// days, and no store accepts less.</summary>
public static class IdempotencyRetention
{
    /// <summary>The floor and the default: 7 days.</summary>
    public static readonly TimeSpan Minimum = TimeSpan.FromDays(7);

    /// <summary>7 days.</summary>
    public static readonly TimeSpan Default = Minimum;

    /// <summary>The environment variable a generated host reads it from, in days.</summary>
    public const string Variable = "DOTNETCQRS_IDEMPOTENCY_RETENTION_DAYS";

    /// <summary><paramref name="retention"/>, or <see cref="Default"/> when null; throws when under the minimum.</summary>
    public static TimeSpan Check(TimeSpan? retention)
    {
        var value = retention ?? Default;
        if (value < Minimum)
            throw new ArgumentOutOfRangeException(nameof(retention), value,
                $"an idempotency record is kept at least {Minimum.TotalDays} days (the gateway contract's G7)");
        return value;
    }

    /// <summary>A whole number of days, at least 7. Null or empty is the default.</summary>
    public static TimeSpan Parse(string? days)
    {
        if (string.IsNullOrWhiteSpace(days))
            return Default;
        if (!int.TryParse(days.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            || n < Minimum.TotalDays || n > 36500)
            throw new InvalidIdempotencySettingException(
                $"{Variable} must be a whole number of days, at least {Minimum.TotalDays}, not '{days}'.");
        return TimeSpan.FromDays(n);
    }

    /// <summary><see cref="Parse"/> of <see cref="Variable"/>.</summary>
    public static TimeSpan FromEnvironment() => Parse(Environment.GetEnvironmentVariable(Variable));
}

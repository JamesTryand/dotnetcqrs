namespace DotnetCqrs.EventStore;

/// <summary>The heartbeat's <c>written_at</c> format: UTC RFC3339 with milliseconds.</summary>
public static class HeartbeatTimestamp
{
    public static string Format(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static DateTimeOffset? Parse(string written) =>
        DateTimeOffset.TryParse(written, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;
}

/// <summary>The writer heartbeat row as a reader sees it (health/telemetry contract section 5).
/// <see cref="WrittenAt"/> is UTC RFC3339 with milliseconds, as stored.</summary>
public sealed record WriterHeartbeat(string WriterNodeId, string WriterOpsUrl, string WrittenAt, long Sequence);

/// <summary>
/// The writer heartbeat (health/telemetry contract section 5): one row, upserted by the writer every
/// few seconds into a table beside the event log, so it replicates with it and a reader can tell how
/// far behind its copy is, even on an idle system. It is never an event: the table is not read by
/// anything that reads events, and writing it appends nothing to the log.
/// </summary>
public interface IHeartbeatStore
{
    /// <summary>Upserts the row: this writer's node id and ops URL, <paramref name="writtenAt"/>, and
    /// a sequence one past the previous row's.</summary>
    Task WriteHeartbeatAsync(string writerNodeId, string writerOpsUrl, DateTimeOffset writtenAt, CancellationToken ct = default);

    /// <summary>The row, or null when there is none to see (no writer has written one yet, or the
    /// table does not exist in this copy).</summary>
    Task<WriterHeartbeat?> ReadHeartbeatAsync(CancellationToken ct = default);
}

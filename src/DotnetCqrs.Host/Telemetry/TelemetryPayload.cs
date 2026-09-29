using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace DotnetCqrs.Host.Telemetry;

/// <summary>
/// The telemetry snapshot (health/telemetry contract section 8.3): one UTF-8 JSON object with the
/// same figures <c>/metrics</c> would return at that moment. <c>series</c> has an entry for every
/// series of section 6; a gauge or counter sample is <c>{"labels": {...}, "value": n}</c> and a
/// histogram sample <c>{"labels": {...}, "buckets": {"&lt;le&gt;": n, ...}, "sum": n, "count": n}</c>.
/// A value that is not known (<see cref="double.NaN"/> in <c>/metrics</c>) is <c>null</c>.
/// </summary>
public static class TelemetryPayload
{
    /// <summary>The timestamp format <c>sent_at</c> uses: UTC RFC 3339 with milliseconds.</summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static byte[] Serialize(MetricsSnapshot snapshot, string nodeId, string role, DateTimeOffset sentAt)
    {
        var buffer = new ArrayBufferWriter<byte>(8192);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("contract_version", NodeHealth.ContractVersion);
            w.WriteString("node_id", nodeId);
            w.WriteString("role", role);
            w.WriteString("sent_at", sentAt.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            w.WriteStartObject("series");
            foreach (var family in snapshot.Families)
            {
                w.WriteStartArray(family.Name);
                foreach (var sample in family.Samples)
                {
                    w.WriteStartObject();
                    Labels(w, sample.Labels);
                    Number(w, "value", sample.Value);
                    w.WriteEndObject();
                }
                foreach (var h in family.Histograms)
                {
                    w.WriteStartObject();
                    Labels(w, h.Labels);
                    w.WriteStartObject("buckets");
                    for (var i = 0; i < NodeMetrics.DurationBuckets.Count; i++)
                        w.WriteNumber(NodeMetrics.Number(NodeMetrics.DurationBuckets[i]), h.Buckets[i]);
                    w.WriteNumber("+Inf", h.Count);
                    w.WriteEndObject();
                    Number(w, "sum", h.Sum);
                    w.WriteNumber("count", h.Count);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void Labels(Utf8JsonWriter w, IReadOnlyList<(string Name, string Value)> labels)
    {
        w.WriteStartObject("labels");
        foreach (var (name, value) in labels)
            w.WriteString(name, value);
        w.WriteEndObject();
    }

    private static void Number(Utf8JsonWriter w, string name, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            w.WriteNull(name);
        else
            w.WriteNumber(name, value);
    }
}

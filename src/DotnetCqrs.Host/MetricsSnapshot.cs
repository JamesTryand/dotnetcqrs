namespace DotnetCqrs.Host;

/// <summary>One sample of a gauge or counter: its labels in order, and its value
/// (<see cref="double.NaN"/> when not known).</summary>
public sealed record MetricSample(IReadOnlyList<(string Name, string Value)> Labels, double Value);

/// <summary>One label set of a histogram family: cumulative counts per
/// <see cref="NodeMetrics.DurationBuckets"/> boundary, then the total count and the sum.</summary>
public sealed record HistogramSample(
    IReadOnlyList<(string Name, string Value)> Labels, IReadOnlyList<long> Buckets, long Count, double Sum);

/// <summary>A series of the health/telemetry contract's section 6: gauges and counters carry
/// <see cref="Samples"/>, a histogram carries <see cref="Histograms"/>.</summary>
public sealed record MetricFamily(
    string Name, string Type, string Help, IReadOnlyList<MetricSample> Samples, IReadOnlyList<HistogramSample> Histograms)
{
    public MetricFamily(string name, string type, string help, IReadOnlyList<MetricSample> samples)
        : this(name, type, help, samples, []) { }
}

/// <summary>Every section-6 series at one moment, in the contract's order.</summary>
public sealed record MetricsSnapshot(IReadOnlyList<MetricFamily> Families);

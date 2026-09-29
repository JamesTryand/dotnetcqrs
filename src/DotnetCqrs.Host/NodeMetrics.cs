using System.Globalization;
using System.Text;
using DotnetCqrs.Consumers;

namespace DotnetCqrs.Host;

/// <summary>
/// The <c>GET /metrics</c> series (health/telemetry contract sections 6 and 7), identical to
/// pocketcqrs's, in the Prometheus text exposition format. Owned by <see cref="NodeHealth"/>, so
/// it exists from process start and every series is present from the first scrape; counters start
/// at zero for every <c>status</c> label and reset on restart.
///
/// <para>Label values are bounded by names known when the host was generated (consumers, read
/// models, dependencies) or by the contract's enumerations, never by an aggregate or stream id.
/// Families whose label values are not known yet (consumers, while booting) are listed with no
/// samples.</para>
/// </summary>
public sealed class NodeMetrics
{
    /// <summary>The command-outcome labels (contract section 7), in the contract's order.</summary>
    public static readonly IReadOnlyList<string> Outcomes = ["accepted", "rejected", "conflict", "unavailable", "error"];

    /// <summary>The fixed <c>cqrs_command_duration_seconds</c> bucket boundaries (section 6.4).</summary>
    public static readonly IReadOnlyList<double> DurationBuckets =
        [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30];

    private static readonly string[] ReadinessStatuses = ["ready", "degraded", "not_ready"];
    private static readonly string[] ConsumerStates = ["current", "behind", "blocked"];

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Histogram> _commands = Outcomes.ToDictionary(o => o, _ => new Histogram());
    private long _eventsAppended;
    private volatile Func<CancellationToken, Task<long>>? _deadLetterDepth;

    /// <summary>The <c>status</c> label for a gateway response (contract section 7): 2xx accepted;
    /// 409 conflict; 503 unavailable; 500, 502 and 504 error; 400, 401, 403, 404, 410 and 422
    /// rejected. Any other 4xx counts as rejected and any other 5xx as error.</summary>
    public static string OutcomeOf(int httpStatus) => httpStatus switch
    {
        >= 200 and < 300 => "accepted",
        409 => "conflict",
        503 => "unavailable",
        >= 400 and < 500 => "rejected",
        _ => "error",
    };

    /// <summary>Counts and times one command this node decided, from receipt to response. A
    /// command a reader forwards is recorded only by the writer that decides it.</summary>
    public void RecordCommand(int httpStatus, TimeSpan duration)
    {
        lock (_gate)
            _commands[OutcomeOf(httpStatus)].Observe(duration.TotalSeconds);
    }

    /// <summary>Counts one event appended by this node.</summary>
    public void EventAppended() => Interlocked.Increment(ref _eventsAppended);

    /// <summary>Where <c>cqrs_deadletter_depth</c> comes from: a read of the unresolved dead
    /// letters. Until it is set, or when it fails, the gauge is NaN (unknown).</summary>
    public void SetDeadLetterDepth(Func<CancellationToken, Task<long>> depth) => _deadLetterDepth = depth;

    /// <summary>The <c>/metrics</c> body. <paramref name="health"/> supplies identity, readiness
    /// and the consumers; it never writes anything.</summary>
    public async Task<string> RenderAsync(NodeHealth health, CancellationToken ct = default)
    {
        var b = new StringBuilder();
        var identity = health.Identity;
        var (_, readyz) = health.Readyz();
        var readiness = (string)readyz["status"]!;
        var checks = (IReadOnlyDictionary<string, object?>)readyz["checks"]!;
        var dependencies = (IReadOnlyDictionary<string, string>)checks["dependencies"]!;
        var consumers = health.Consumers();

        Family(b, "cqrs_node_info", "gauge", "Node identity (node-identity contract); always 1.");
        Sample(b, "cqrs_node_info",
            [("node_id", identity?.NodeId ?? ""), ("instance", identity?.Instance ?? ""), ("host", health.Host),
             ("role", identity?.Role ?? ""), ("stack", health.Stack), ("contract_version", NodeHealth.ContractVersion)], 1);

        Family(b, "cqrs_readiness_status", "gauge", "Current /readyz status, one-hot.");
        foreach (var status in ReadinessStatuses)
            Sample(b, "cqrs_readiness_status", [("status", status)], status == readiness ? 1 : 0);

        Dictionary<string, (long Count, double Sum, long[] Buckets)> commands;
        lock (_gate)
            commands = _commands.ToDictionary(c => c.Key, c => (c.Value.Count, c.Value.Sum, c.Value.Buckets.ToArray()));

        Family(b, "cqrs_commands_total", "counter", "Commands decided on this node, by outcome.");
        foreach (var outcome in Outcomes)
            Sample(b, "cqrs_commands_total", [("status", outcome)], commands[outcome].Count);

        Family(b, "cqrs_command_duration_seconds", "histogram", "Command receipt to response, on the node that decides.");
        foreach (var outcome in Outcomes)
        {
            var (count, sum, buckets) = commands[outcome];
            for (var i = 0; i < DurationBuckets.Count; i++)
                Sample(b, "cqrs_command_duration_seconds_bucket", [("status", outcome), ("le", Number(DurationBuckets[i]))], buckets[i]);
            Sample(b, "cqrs_command_duration_seconds_bucket", [("status", outcome), ("le", "+Inf")], count);
            Sample(b, "cqrs_command_duration_seconds_sum", [("status", outcome)], sum);
            Sample(b, "cqrs_command_duration_seconds_count", [("status", outcome)], count);
        }

        Family(b, "cqrs_events_appended_total", "counter", "Events appended by this node.");
        Sample(b, "cqrs_events_appended_total", [], Interlocked.Read(ref _eventsAppended));

        Family(b, "cqrs_write_lag_seconds", "gauge", "Age of the writer heartbeat this node sees; 0 on a writer.");
        Sample(b, "cqrs_write_lag_seconds", [], Convert.ToDouble(checks["write_lag_seconds"], CultureInfo.InvariantCulture));

        Family(b, "cqrs_projection_lag_seconds", "gauge", "Age of the oldest event each read model has not applied.");
        foreach (var c in consumers.Where(c => c.IsReadModel))
            Sample(b, "cqrs_projection_lag_seconds", [("read_model", c.Name)], c.LagSeconds ?? double.NaN);

        Family(b, "cqrs_consumer_lag", "gauge", "Positions each consumer is behind the log head.");
        foreach (var c in consumers)
            Sample(b, "cqrs_consumer_lag", [("consumer", c.Name)], c.LagPositions is { } lag ? lag : double.NaN);

        Family(b, "cqrs_consumer_state", "gauge", "Each consumer's state, one-hot.");
        foreach (var c in consumers)
            foreach (var state in ConsumerStates)
                Sample(b, "cqrs_consumer_state", [("consumer", c.Name), ("state", state)],
                    state == c.State.ToString().ToLowerInvariant() ? 1 : 0);

        Family(b, "cqrs_dependency_up", "gauge", "Each required dependency: 1 up, 0 down.");
        foreach (var (name, state) in dependencies)
            Sample(b, "cqrs_dependency_up", [("dependency", name)], state == "up" ? 1 : 0);

        Family(b, "cqrs_deadletter_depth", "gauge", "Unresolved dead letters.");
        Sample(b, "cqrs_deadletter_depth", [], await DeadLetterDepthAsync(ct));

        return b.ToString();
    }

    private async Task<double> DeadLetterDepthAsync(CancellationToken ct)
    {
        if (_deadLetterDepth is not { } depth)
            return double.NaN;
        try
        {
            return await depth(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return double.NaN;
        }
    }

    private static void Family(StringBuilder b, string name, string type, string help) =>
        b.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n')
         .Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');

    private static void Sample(StringBuilder b, string name, (string Name, string Value)[] labels, double value)
    {
        b.Append(name);
        if (labels.Length > 0)
        {
            b.Append('{');
            for (var i = 0; i < labels.Length; i++)
            {
                if (i > 0) b.Append(',');
                b.Append(labels[i].Name).Append("=\"").Append(Escape(labels[i].Value)).Append('"');
            }
            b.Append('}');
        }
        b.Append(' ').Append(Number(value)).Append('\n');
    }

    private static string Number(double value) =>
        double.IsNaN(value) ? "NaN"
        : double.IsPositiveInfinity(value) ? "+Inf"
        : double.IsNegativeInfinity(value) ? "-Inf"
        : value.ToString("R", CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal)
             .Replace("\n", "\\n", StringComparison.Ordinal);

    private sealed class Histogram
    {
        public long Count { get; private set; }
        public double Sum { get; private set; }
        public long[] Buckets { get; } = new long[DurationBuckets.Count];

        public void Observe(double seconds)
        {
            Count++;
            Sum += seconds;
            for (var i = 0; i < DurationBuckets.Count; i++)
                if (seconds <= DurationBuckets[i])
                    Buckets[i]++;
        }
    }
}

using System.Diagnostics;
using System.Net;
using DotnetCqrs.Consumers;

namespace DotnetCqrs.Host;

/// <summary>Where a node is in its life, as the health/telemetry contract names it
/// (<c>STATE-MACHINES.md</c>, machine 1, <c>NodeLifecycle</c>). Stopped and failed need no value:
/// a process that has exited answers nothing.</summary>
public enum NodeLifecycleState
{
    /// <summary>From process start to the main traffic port listening.</summary>
    Booting,

    /// <summary>Listening, consumers started, read models not yet within threshold.</summary>
    CatchingUp,

    Serving,

    /// <summary>Shutting down: readiness closed first, in-flight work finishing.</summary>
    Draining,
}

/// <summary>
/// What a node reports on its ops port, per the cross-stack health/telemetry contract
/// (<c>platform/cqrs-runtime-contract/contracts/health-telemetry.md</c>, 1.0), identical to
/// pocketcqrs's. Created at process start, before anything is configured, so <c>/healthz</c> can
/// answer while the node boots: <c>host</c>, <c>stack</c> and <c>started_at</c> are known at once;
/// the identity is <see langword="null"/> until <see cref="SetIdentity"/>. Thread-safe: the ops
/// server reads it while the host's startup writes it.
///
/// <para><c>/readyz</c> (contract section 4) follows the lifecycle: <c>starting</c> while booting;
/// <see cref="BeginCatchUp"/> once the traffic port listens and the consumers run; serving once
/// every read model is within threshold, or, on a writer, once the catch-up deadline passes.
/// The reporting tables in <c>STATE-MACHINES.md</c> are the spec for every status and reason.</para>
/// </summary>
public sealed class NodeHealth
{
    public const string ContractVersion = "1.0";

    /// <summary>How often a catching-up node re-checks its read models, so it opens readiness (and
    /// logs a passed deadline) without waiting for a probe to ask.</summary>
    private static readonly TimeSpan CatchUpCheckInterval = TimeSpan.FromMilliseconds(250);

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private volatile NodeIdentity? _identity;
    private volatile int _lifecycle = (int)NodeLifecycleState.Booting;
    private volatile Func<IReadOnlyList<ConsumerStatus>>? _consumers;
    private volatile Func<ReplicationStatus>? _replication;
    private volatile DependencyMonitor? _dependencies;
    private DateTimeOffset _catchUpDeadline;
    private bool _catchUpDeadlineLogged;
    private Action<string> _log = _ => { };
    private ITimer? _catchUpTimer;

    /// <summary><paramref name="timeProvider"/> is the clock the catch-up deadline is measured
    /// against (default: the system clock).</summary>
    public NodeHealth(string host, DateTimeOffset startedAt, TimeProvider? timeProvider = null)
    {
        Host = host;
        StartedAt = startedAt;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>For the host's own process: the hostname (<c>unknown</c> if it cannot be read,
    /// per node identity I7) and the process start time.</summary>
    public static NodeHealth ForThisProcess(Action<string>? logError = null) =>
        new(NodeIdentity.HostName(Dns.GetHostName, logError),
            new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero));

    public string Host { get; }

    public string Stack => NodeIdentity.StackName;

    public DateTimeOffset StartedAt { get; }

    /// <summary>Null until the node has resolved it; <c>/healthz</c> reports its fields as
    /// <c>null</c> until then.</summary>
    public NodeIdentity? Identity => _identity;

    public NodeLifecycleState Lifecycle => (NodeLifecycleState)_lifecycle;

    /// <summary>The <c>/metrics</c> series (contract section 6), which exist from process start.</summary>
    public NodeMetrics Metrics { get; } = new();

    /// <summary>The consumer engine's status once boot has completed; empty while booting.</summary>
    public IReadOnlyList<ConsumerStatus> Consumers() => _consumers?.Invoke() ?? [];

    public void SetIdentity(NodeIdentity identity) => _identity = identity;

    public void SetLifecycle(NodeLifecycleState state) => _lifecycle = (int)state;

    /// <summary>A reader's replication freshness (machine 4), usually a
    /// <see cref="ReplicationMonitor"/>'s <see cref="ReplicationMonitor.Current"/>. A reader without
    /// one reports <c>replication_unknown</c>; a writer ignores it.</summary>
    public void SetReplication(Func<ReplicationStatus> status) => _replication = status;

    /// <summary>The node's required dependencies (machine 3). Without one, <c>dependencies</c> is
    /// empty and none contributes.</summary>
    public void SetDependencies(DependencyMonitor dependencies) => _dependencies = dependencies;

    /// <summary>Boot completed (machine 1, <c>BootCompleted</c>): the traffic port listens and the
    /// consumers have started, so the node moves to catching up. <paramref name="consumers"/> is
    /// the consumer engine's <see cref="ConsumerEngine.Status"/>; only read models count. The node
    /// starts serving when every read model is current; if it is a writer and
    /// <paramref name="catchUpDeadline"/> passes first, it starts serving anyway and reports what is
    /// still behind as <c>degraded</c>. A reader keeps catching up. <paramref name="log"/> hears
    /// when readiness opens and when the deadline passes.</summary>
    public void BeginCatchUp(Func<IReadOnlyList<ConsumerStatus>> consumers, TimeSpan catchUpDeadline, Action<string>? log = null)
    {
        lock (_gate)
        {
            if (Lifecycle != NodeLifecycleState.Booting)
                throw new InvalidOperationException($"Boot already completed: the node is {Lifecycle}.");
            _consumers = consumers;
            _catchUpDeadline = _time.GetUtcNow() + catchUpDeadline;
            _log = log ?? (_ => { });
            _lifecycle = (int)NodeLifecycleState.CatchingUp;
            _catchUpTimer = _time.CreateTimer(_ => Refresh(), null, CatchUpCheckInterval, CatchUpCheckInterval);
        }
        Refresh();
    }

    /// <summary>While catching up, moves to serving if every read model is current or, on a
    /// writer, the catch-up deadline has passed (machine 1's <c>InitialCatchUpCompleted</c> and
    /// <c>CatchUpDeadlineReached</c>). Runs on a timer and before every <c>/readyz</c>; a no-op in
    /// any other state.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            if (Lifecycle != NodeLifecycleState.CatchingUp || _consumers is not { } consumers)
                return;
            List<ConsumerStatus> notCurrent;
            try
            {
                notCurrent = consumers().Where(c => c.IsReadModel && c.State != ConsumerState.Current).ToList();
            }
            catch (Exception ex)
            {
                _log($"readiness: could not read consumer status: {ex.Message}");
                return;
            }
            if (notCurrent.Count == 0)
            {
                OpenReadiness("readiness opened: every read model is within its lag threshold");
                return;
            }
            if (_time.GetUtcNow() < _catchUpDeadline)
                return;
            var isWriter = _identity?.Role == "writer";
            if (!_catchUpDeadlineLogged)
            {
                _catchUpDeadlineLogged = true;
                var still = string.Join(", ", notCurrent.Select(c => $"{c.Name} ({c.State.ToString().ToLowerInvariant()})"));
                _log(isWriter
                    ? $"catch-up deadline reached; serving anyway as the writer, still catching up: {still}"
                    : $"catch-up deadline reached; a reader keeps catching up before it serves: {still}");
            }
            if (isWriter)
                OpenReadiness(null);
        }
    }

    private void OpenReadiness(string? message)
    {
        _lifecycle = (int)NodeLifecycleState.Serving;
        _catchUpTimer?.Dispose();
        _catchUpTimer = null;
        if (message is not null)
            _log(message);
    }

    /// <summary>The <c>GET /readyz</c> status code and body (contract section 4): 200 when
    /// <c>ready</c> or <c>degraded</c>, 503 when <c>not_ready</c>. The status is the most severe
    /// contribution of the lifecycle and the read models (<c>STATE-MACHINES.md</c>, "Readiness:"
    /// tables); <c>reasons</c> lists every non-ready one. dotnetcqrs has no maintenance mode and
    /// compiles its deciders in, so <c>mode</c> is always running and <c>functions</c> complete.</summary>
    public (int StatusCode, IReadOnlyDictionary<string, object?> Body) Readyz()
    {
        Refresh();
        var identity = _identity;
        var lifecycle = Lifecycle;
        var contributions = new List<(Readiness Status, string Reason)>();

        switch (lifecycle)
        {
            case NodeLifecycleState.Booting: contributions.Add((Readiness.NotReady, "starting")); break;
            case NodeLifecycleState.CatchingUp: contributions.Add((Readiness.NotReady, "catching_up")); break;
            case NodeLifecycleState.Draining: contributions.Add((Readiness.NotReady, "draining")); break;
        }

        // read_models: the worst state across the node's read models. Behind or blocked is local
        // to this node, so not_ready on a reader; on the sole writer it is degraded, so that a
        // stuck projection never removes the only write authority from the pool.
        var readModels = _consumers?.Invoke().Where(c => c.IsReadModel).ToList() ?? [];
        var worst = readModels.Count == 0 ? ConsumerState.Current : readModels.Max(c => c.State);
        if (worst != ConsumerState.Current)
        {
            var severity = identity?.Role == "writer" ? Readiness.Degraded : Readiness.NotReady;
            contributions.Add((severity, worst == ConsumerState.Blocked ? "projection_blocked" : "projection_behind"));
        }

        // replication (readers only): stale because this reader is behind is local, not_ready;
        // stale because the writer is down is shared, degraded; never having seen a heartbeat
        // leaves nothing trustworthy to serve, not_ready.
        var writeLag = 0.0;
        if (identity?.Role == "reader")
        {
            var replication = _replication?.Invoke() ?? new ReplicationStatus(ReplicationState.Unknown, 0);
            writeLag = replication.WriteLagSeconds;
            switch (replication.State)
            {
                case ReplicationState.Unknown: contributions.Add((Readiness.NotReady, "replication_unknown")); break;
                case ReplicationState.StaleWriterUp: contributions.Add((Readiness.NotReady, "replication_stale")); break;
                case ReplicationState.StaleWriterDown: contributions.Add((Readiness.Degraded, "replication_stale")); break;
            }
        }

        // event_store: this node's own store is local, so not_ready on a reader; the sole writer
        // stays in the pool as degraded. shared_dependencies (KMS, the writer on a reader) fail
        // every node at once, so degraded, one reason however many are down.
        var dependencies = _dependencies?.States() ?? [];
        foreach (var (name, up) in dependencies.Where(d => !d.Up && d.Name == DependencyMonitor.EventStore))
            contributions.Add((identity?.Role == "writer" ? Readiness.Degraded : Readiness.NotReady, "event_store_unavailable"));
        if (dependencies.Any(d => !d.Up && d.Name != DependencyMonitor.EventStore))
            contributions.Add((Readiness.Degraded, "dependency_unavailable"));

        var status = contributions.Count == 0 ? Readiness.Ready : contributions.Max(c => c.Status);
        var body = new Dictionary<string, object?>
        {
            ["status"] = status switch
            {
                Readiness.Ready => "ready",
                Readiness.Degraded => "degraded",
                _ => "not_ready",
            },
            ["role"] = identity?.Role,
            ["node_id"] = identity?.NodeId,
            ["contract_version"] = ContractVersion,
            ["reasons"] = contributions.Select(c => c.Reason).ToList(),
            ["checks"] = new Dictionary<string, object?>
            {
                // A writer is the replication source, so 0; a reader, the heartbeat's age.
                ["write_lag_seconds"] = Math.Round(writeLag, 3),
                ["projection_lag_seconds"] = Math.Round(readModels.Select(c => c.LagSeconds ?? 0).DefaultIfEmpty(0).Max(), 3),
                ["dependencies"] = dependencies.ToDictionary(d => d.Name, d => d.Up ? "up" : "down"),
            },
        };
        return (status == Readiness.NotReady ? 503 : 200, body);
    }

    /// <summary>In order of severity, so the most severe contribution is the maximum.</summary>
    private enum Readiness
    {
        Ready,
        Degraded,
        NotReady,
    }

    /// <summary>The <c>GET /healthz</c> body (contract section 3), fields in the contract's order.
    /// <c>node_id</c>, <c>identity</c>, <c>instance</c> and <c>role</c> are null while booting;
    /// once the node has booted every field is set.</summary>
    public IReadOnlyDictionary<string, object?> HealthzBody()
    {
        var identity = _identity;
        return new Dictionary<string, object?>
        {
            ["status"] = "alive",
            ["contract_version"] = ContractVersion,
            ["node_id"] = identity?.NodeId,
            ["identity"] = identity?.IdentityValue,
            ["instance"] = identity?.Instance,
            ["host"] = Host,
            ["stack"] = Stack,
            ["role"] = identity?.Role,
            ["started_at"] = StartedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}

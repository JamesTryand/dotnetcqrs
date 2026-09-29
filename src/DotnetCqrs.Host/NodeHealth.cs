using System.Diagnostics;
using System.Net;

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
/// </summary>
public sealed class NodeHealth
{
    public const string ContractVersion = "1.0";

    private volatile NodeIdentity? _identity;
    private volatile int _lifecycle = (int)NodeLifecycleState.Booting;

    public NodeHealth(string host, DateTimeOffset startedAt)
    {
        Host = host;
        StartedAt = startedAt;
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

    public void SetIdentity(NodeIdentity identity) => _identity = identity;

    public void SetLifecycle(NodeLifecycleState state) => _lifecycle = (int)state;

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

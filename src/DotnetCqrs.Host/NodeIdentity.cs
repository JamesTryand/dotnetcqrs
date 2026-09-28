using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DotnetCqrs.Host;

/// <summary>How a node came by its <see cref="NodeIdentity.NodeId"/>, reported as
/// <c>identity</c> (see <see cref="NodeIdentity.IdentityValue"/>).</summary>
public enum NodeIdentitySource
{
    /// <summary>From <c>CQRS_NODE_ID</c>: explicitly set by an orchestrator, so intentional.</summary>
    Assigned,

    /// <summary>Read from, or just written to, the <c>node-id</c> file in the state directory. It
    /// says nothing about whether that directory survives a restart.</summary>
    Persistent,

    /// <summary>Generated for this process only; it will not survive a restart.</summary>
    Ephemeral,
}

/// <summary>
/// Who this node is, per the cross-stack node-identity contract
/// (<c>platform/cqrs-runtime-contract/contracts/node-identity.md</c>, 1.0), identical to
/// pocketcqrs's: an opaque <see cref="NodeId"/> that survives restarts and tells apart several
/// nodes on one machine, plus descriptive attributes that sit beside it and are not part of it.
///
/// <para>Resolved once, at boot, by <see cref="Resolve"/> / <see cref="FromEnvironment"/>:
/// <c>CQRS_NODE_ID</c> if set (never touching the file); else the <c>node-id</c> file in the
/// node-local state directory (<c>CQRS_STATE_DIR</c>); else a new UUIDv7, written there
/// atomically if the directory allows it. A <c>node-id</c> file that can't be used is never
/// repaired or overwritten: the node runs with an ephemeral id and says so, every boot, until an
/// operator fixes it. An invalid <c>CQRS_NODE_ID</c> fails the boot.</para>
///
/// <para>The state directory MUST be node-local and never replicated: a reader replicating the
/// writer's store would otherwise inherit the writer's id. Hence it is not <c>data/</c>, where the
/// SQLite event log lives, and there is no default: unset, identity is ephemeral.</para>
/// </summary>
public sealed partial record NodeIdentity(
    string NodeId,
    NodeIdentitySource Identity,
    string Instance,
    string Host,
    string Stack,
    string Role,
    DateTimeOffset StartedAt)
{
    public const string NodeIdVariable = "CQRS_NODE_ID";
    public const string StateDirVariable = "CQRS_STATE_DIR";
    public const string NodeIdFileName = "node-id";
    public const string StackName = "dotnetcqrs";
    public const string InstanceVariable = "CQRS_INSTANCE";

    /// <summary>Reported as <see cref="Host"/> when the hostname cannot be read (contract I7).</summary>
    public const string UnknownHost = "unknown";

    /// <summary><see cref="Identity"/> as the contract reports it: <c>assigned</c>,
    /// <c>persistent</c> or <c>ephemeral</c>.</summary>
    public string IdentityValue => Identity switch
    {
        NodeIdentitySource.Assigned => "assigned",
        NodeIdentitySource.Persistent => "persistent",
        _ => "ephemeral",
    };

    /// <summary><see cref="StartedAt"/> as the contract reports it: UTC RFC3339 with
    /// milliseconds.</summary>
    public string StartedAtValue =>
        StartedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A <c>node_id</c> is one literal token on NATS, Kafka, RabbitMQ and MQTT, a
    /// Prometheus label value and a file's content (contract section 1).</summary>
    public static bool IsValidNodeId(string? value) => value is not null && NodeIdFormat().IsMatch(value);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex NodeIdFormat();

    /// <summary>
    /// The contract's resolution table (section 3), as a function of its three inputs:
    /// <paramref name="assignedId"/> (<c>CQRS_NODE_ID</c>; <see langword="null"/> or empty is
    /// unset) and <paramref name="stateDirectory"/> (<see langword="null"/> when none is
    /// configured, which behaves as absent and unwritable). Throws
    /// <see cref="InvalidNodeIdException"/> for an invalid assigned id: the boot fails.
    /// </summary>
    public static NodeIdResolution Resolve(string? assignedId, INodeStateDirectory? stateDirectory)
    {
        if (!string.IsNullOrEmpty(assignedId))
        {
            if (!IsValidNodeId(assignedId))
                throw new InvalidNodeIdException(assignedId);
            // Explicitly set values are intentional; the stored id is the default and is neither
            // read nor written, so removing the assignment returns the node to it.
            return new(assignedId, NodeIdentitySource.Assigned, null, false);
        }

        if (stateDirectory is null)
            return new(NewId(), NodeIdentitySource.Ephemeral,
                $"node identity: no state directory configured ({StateDirVariable}), so this node gets a new id on "
                + $"every start; set {StateDirVariable} to a node-local directory, or {NodeIdVariable}", false);

        var stored = stateDirectory.ReadNodeId();
        switch (stored.State)
        {
            case StoredNodeIdState.Valid:
                return new(stored.NodeId!, NodeIdentitySource.Persistent, null, false);
            case StoredNodeIdState.Invalid:
            case StoredNodeIdState.Unreadable:
                // Never repaired: overwriting would silently make this a new node and destroy the
                // evidence. Every boot is ephemeral, and says so, until an operator fixes it.
                return new(NewId(), NodeIdentitySource.Ephemeral,
                    $"node identity: {stateDirectory.NodeIdPath} is {(stored.State == StoredNodeIdState.Invalid ? "not a valid node id" : "unreadable")}"
                    + $"{(stored.Detail is null ? "" : $" ({stored.Detail})")}; running with an ephemeral id and leaving the file "
                    + "untouched. Remove it (a new id is generated) or write a valid id into it.", true);
        }

        var generated = NewId();
        return stateDirectory.TryCreateNodeId(generated, out var reason)
            ? new(generated, NodeIdentitySource.Persistent, null, false)
            : new(generated, NodeIdentitySource.Ephemeral,
                $"node identity: could not write {stateDirectory.NodeIdPath} ({reason}), so this node gets a new id on every start",
                false);
    }

    /// <summary>
    /// Resolves identity from <c>CQRS_NODE_ID</c> and <c>CQRS_STATE_DIR</c> and adds the
    /// descriptive attributes: <c>instance</c> is <c>CQRS_INSTANCE</c> if set, else
    /// <paramref name="instance"/>. Writes any warning or error to <paramref name="logError"/>
    /// and one summary line to <paramref name="log"/>. <paramref name="startedAt"/> defaults to
    /// this process's start time. Throws <see cref="InvalidIdentitySettingException"/> for an
    /// invalid <c>CQRS_NODE_ID</c> or <c>CQRS_INSTANCE</c>, before anything is written.
    /// </summary>
    public static NodeIdentity FromEnvironment(
        string instance, string role, Action<string>? log = null, Action<string>? logError = null,
        DateTimeOffset? startedAt = null)
    {
        var workload = ResolveInstance(Environment.GetEnvironmentVariable(InstanceVariable), instance);
        var stateDir = Environment.GetEnvironmentVariable(StateDirVariable);
        var resolution = Resolve(
            Environment.GetEnvironmentVariable(NodeIdVariable),
            string.IsNullOrEmpty(stateDir) ? null : new DiskNodeStateDirectory(stateDir));
        if (resolution.Notice is not null)
            logError?.Invoke(resolution.Notice);

        var identity = new NodeIdentity(
            resolution.NodeId, resolution.Identity, workload, HostName(Dns.GetHostName, logError), StackName, role,
            startedAt ?? new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero));
        log?.Invoke(identity.ToString());
        return identity;
    }

    /// <summary><c>instance</c> (contract I6): <paramref name="configured"/>
    /// (<c>CQRS_INSTANCE</c>; null or empty is unset) if set, else <paramref name="fallback"/>, the
    /// workload's own name. A configured one has <c>node_id</c>'s format, or the boot fails.</summary>
    public static string ResolveInstance(string? configured, string fallback)
    {
        if (string.IsNullOrEmpty(configured))
            return fallback;
        return IsValidNodeId(configured) ? configured : throw new InvalidInstanceException(configured);
    }

    /// <summary><c>host</c> (contract I7): the hostname, or <see cref="UnknownHost"/> with a
    /// warning when it cannot be read. Never a boot failure: host describes the node, it does
    /// not identify it.</summary>
    public static string HostName(Func<string> readHostName, Action<string>? logError = null)
    {
        try
        {
            var host = readHostName();
            if (!string.IsNullOrEmpty(host))
                return host;
            logError?.Invoke($"node identity: the hostname is empty; reporting host={UnknownHost}");
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException or IOException)
        {
            logError?.Invoke($"node identity: cannot read the hostname ({ex.Message}); reporting host={UnknownHost}");
        }
        return UnknownHost;
    }

    /// <summary>A new canonical, lowercase, hyphenated UUIDv7, which matches the format.</summary>
    public static string NewId() => Guid.CreateVersion7().ToString("D");

    public override string ToString() =>
        $"node identity: node_id={NodeId} identity={IdentityValue} instance={Instance} host={Host} "
        + $"stack={Stack} role={Role} started_at={StartedAtValue}";
}

/// <summary>What <see cref="NodeIdentity.Resolve"/> decided. <see cref="Notice"/> is set when
/// the operator should know something: a warning, or an error when <see cref="NoticeIsError"/>.</summary>
public sealed record NodeIdResolution(string NodeId, NodeIdentitySource Identity, string? Notice, bool NoticeIsError);

/// <summary>An invalid identity setting: the boot fails, like any other invalid configuration.</summary>
public abstract class InvalidIdentitySettingException(string message) : Exception(message);

/// <summary>An invalid <c>CQRS_NODE_ID</c>.</summary>
public sealed class InvalidNodeIdException(string value) : InvalidIdentitySettingException(
    $"{NodeIdentity.NodeIdVariable} '{value}' is not a valid node id: use 1-64 letters, digits, '_' or '-'.")
{
    public string Value { get; } = value;
}

/// <summary>An invalid <c>CQRS_INSTANCE</c> (same format as a node id).</summary>
public sealed class InvalidInstanceException(string value) : InvalidIdentitySettingException(
    $"{NodeIdentity.InstanceVariable} '{value}' is not a valid instance name: use 1-64 letters, digits, '_' or '-'.")
{
    public string Value { get; } = value;
}

/// <summary>The <c>node-id</c> file as a boot finds it.</summary>
public enum StoredNodeIdState
{
    Absent,
    Valid,

    /// <summary>Present and readable, but empty or not matching the format.</summary>
    Invalid,

    /// <summary>Present, but reading it fails.</summary>
    Unreadable,
}

public sealed record StoredNodeId(StoredNodeIdState State, string? NodeId = null, string? Detail = null);

/// <summary>The node-local state directory, as far as identity needs it. A seam so the
/// resolution table can be exercised whole; <see cref="DiskNodeStateDirectory"/> is the real
/// one.</summary>
public interface INodeStateDirectory
{
    /// <summary>Where the <c>node-id</c> file is, for messages.</summary>
    string NodeIdPath { get; }

    StoredNodeId ReadNodeId();

    /// <summary>Writes <paramref name="nodeId"/> as a new <c>node-id</c> file, atomically, and
    /// never over an existing one. False, with a reason, when it can't.</summary>
    bool TryCreateNodeId(string nodeId, out string? reason);
}

/// <summary>A state directory on disk. Creates the directory on first write if it doesn't exist.</summary>
public sealed class DiskNodeStateDirectory(string path) : INodeStateDirectory
{
    public string Path { get; } = path;

    public string NodeIdPath => System.IO.Path.Combine(Path, NodeIdentity.NodeIdFileName);

    public StoredNodeId ReadNodeId()
    {
        // "Present" means anything at that path: a directory named node-id is present but can't
        // be read as the file, which is the unreadable case, not the absent one.
        if (!File.Exists(NodeIdPath) && !Directory.Exists(NodeIdPath))
            return new(StoredNodeIdState.Absent);
        string content;
        try
        {
            content = File.ReadAllText(NodeIdPath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(StoredNodeIdState.Unreadable, Detail: ex.Message);
        }
        var trimmed = content.Trim();
        return NodeIdentity.IsValidNodeId(trimmed)
            ? new(StoredNodeIdState.Valid, trimmed)
            : new(StoredNodeIdState.Invalid, Detail: trimmed.Length == 0 ? "empty" : null);
    }

    public bool TryCreateNodeId(string nodeId, out string? reason)
    {
        var temp = System.IO.Path.Combine(Path, $".{NodeIdentity.NodeIdFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Path);
            // Write beside the target, flush, then rename: a crash leaves either no node-id or a
            // whole one, never half an id. overwrite: false keeps an existing file untouched.
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(nodeId + "\n");
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, NodeIdPath, overwrite: false);
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            reason = ex.Message;
            return false;
        }
    }
}

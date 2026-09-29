namespace DotnetCqrs.Host;

/// <summary>
/// A node's required dependencies (health/telemetry contract section 4.6, <c>STATE-MACHINES.md</c>
/// machine 3, <c>RequiredDependency</c>): the event store; the KMS facade when the domain holds
/// personal data; the writer, on a reader. Each is checked on a loop; a check is a delegate that
/// throws when the dependency is not usable. A dependency is <c>up</c> after a successful check and
/// <c>down</c> after <see cref="FailuresToDown"/> consecutive failures (or after a failed first
/// check). Run <see cref="CheckAllAsync"/> once before boot completes, so no dependency is left
/// unchecked once the node serves. NATS is never a required dependency.
/// </summary>
public sealed class DependencyMonitor(int failuresToDown = DependencyMonitor.DefaultFailuresToDown, Action<string>? log = null)
{
    /// <summary>The event store's name in <c>/readyz</c>'s <c>dependencies</c>; the others are shared.</summary>
    public const string EventStore = "event_store";
    public const string Kms = "kms";
    public const string Writer = "writer";

    public const int DefaultFailuresToDown = 3;

    private readonly Lock _gate = new();
    private readonly List<(string Name, Func<CancellationToken, Task> Check)> _checks = [];
    private readonly Dictionary<string, (bool Up, int Failures)> _states = new(StringComparer.Ordinal);

    public int FailuresToDown { get; } = Math.Max(1, failuresToDown);

    /// <summary>Adds a dependency and how to check it.</summary>
    public void Add(string name, Func<CancellationToken, Task> check)
    {
        lock (_gate)
            _checks.Add((name, check));
    }

    /// <summary>Every dependency checked so far: true is up. The event store first, then by name.</summary>
    public IReadOnlyList<(string Name, bool Up)> States()
    {
        lock (_gate)
            return _states.OrderBy(s => s.Key == EventStore ? 0 : 1).ThenBy(s => s.Key, StringComparer.Ordinal)
                .Select(s => (s.Key, s.Value.Up)).ToList();
    }

    /// <summary>Checks every dependency once and records the result.</summary>
    public async Task CheckAllAsync(CancellationToken ct = default)
    {
        List<(string Name, Func<CancellationToken, Task> Check)> checks;
        lock (_gate)
            checks = [.. _checks];
        foreach (var (name, check) in checks)
        {
            bool ok;
            try
            {
                await check(ct);
                ok = true;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                ok = false;
            }
            Record(name, ok);
        }
    }

    /// <summary>Checks every <paramref name="interval"/> until cancelled.</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAllAsync(ct);
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Applies one check's outcome (machine 3).</summary>
    public void Record(string name, bool ok)
    {
        string? message = null;
        lock (_gate)
        {
            var known = _states.TryGetValue(name, out var state);
            if (ok)
            {
                if (known && !state.Up) message = $"dependency {name}: up again";
                _states[name] = (true, 0);
            }
            else
            {
                var failures = (known ? state.Failures : 0) + 1;
                var down = !known || !state.Up || failures >= FailuresToDown;
                if (down && (!known || state.Up)) message = $"dependency {name}: down";
                _states[name] = (!down, failures);
            }
        }
        if (message is not null) log?.Invoke(message);
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Consumers;

/// <summary>
/// Polls an event source and feeds every registered consumer independently,
/// each with its own durable checkpoint. This is the plumbing projections and
/// reactors both build on, per the concepts doc: "a durable consumer that
/// tracks its own read position and catches up reliably after a restart."
/// </summary>
public sealed class ConsumerEngine
{
    private readonly IPollSource _source;
    private readonly ICheckpointStore _checkpoints;
    private readonly TimeSpan _tick;
    private readonly Action<string> _log;
    private readonly TimeProvider _time;

    // Each consumer's progress as of its latest pass, for Status(); written only by the pass.
    private readonly ConcurrentDictionary<string, Progress> _progress = new(StringComparer.Ordinal);

    private readonly Lock _consumersLock = new();
    private readonly List<Registration> _consumers = [];

    // Set by StopAsync (health/telemetry machine 1, Draining): the loop ends, and a pass stops after
    // the event in hand, rather than running on until it is caught up. _hardStop is what a passed
    // deadline cancels; it is linked to the token StartAsync was given.
    private volatile bool _stopRequested;

    // One catch-up pass at a time. The background loop (StartAsync) and a caller's own RunOnceAsync
    // would otherwise both read the same checkpoints and each apply every pending event: twice
    // delivered, and concurrently, to a consumer that was only ever promised one event at a time.
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private CancellationTokenSource? _hardStop;
    private Task? _loop;

    // Bounded to 1 and drops on a full channel: the same "non-blocking nudge,
    // coalesce bursts" shape as pocketcqrs's buffered-channel-with-default-case.
    private readonly Channel<byte> _nudge = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Creates an engine that polls <paramref name="source"/> and checkpoints
    /// against <paramref name="checkpoints"/> — pass the same <see cref="SqliteEventStore"/>
    /// for both in the ordinary single-node case. <paramref name="tick"/> is the fallback
    /// poll interval (default 1s); <paramref name="logger"/> receives run-error messages
    /// (default: discarded); <paramref name="timeProvider"/> is the clock lag is measured
    /// against (default: the system clock).</summary>
    public ConsumerEngine(
        IPollSource source, ICheckpointStore checkpoints, TimeSpan? tick = null, Action<string>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _source = source;
        _checkpoints = checkpoints;
        _tick = tick ?? TimeSpan.FromSeconds(1);
        _log = logger ?? (_ => { });
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The default <see cref="LagThreshold"/>.</summary>
    public static readonly TimeSpan DefaultLagThreshold = TimeSpan.FromSeconds(5);

    /// <summary>How old the oldest unapplied event may be before a consumer counts as
    /// <see cref="ConsumerState.Behind"/> rather than <see cref="ConsumerState.Current"/>.</summary>
    public TimeSpan LagThreshold { get; init; } = DefaultLagThreshold;

    /// <summary>Adds a consumer, checkpointed in the engine's store.</summary>
    public void Register(IConsumer consumer) => Register(consumer, _checkpoints);

    /// <summary>Adds a consumer whose position lives in <paramref name="checkpoints"/>
    /// instead of the engine's store. For a consumer whose state belongs to this process
    /// rather than the database (an in-memory cache, say): give it an
    /// <see cref="InMemoryCheckpointStore"/>, so no other instance can move its position
    /// and a restart starts it afresh.</summary>
    public void Register(IConsumer consumer, ICheckpointStore checkpoints)
    {
        lock (_consumersLock)
            _consumers.Add(new Registration(consumer, checkpoints));
    }

    /// <summary>Drops the consumer with <paramref name="name"/> (no-op if absent). The
    /// durable checkpoint is kept, so re-registering later resumes where it left off.</summary>
    public void Unregister(string name)
    {
        lock (_consumersLock)
            _consumers.RemoveAll(r => r.Consumer.Name == name);
        _progress.TryRemove(name, out _);
    }

    /// <summary>Every registered consumer's state and lag (health/telemetry contract sections 4.4
    /// and 4.5), sorted by name. Read from what the passes last recorded, so it never touches
    /// the store: the lag in seconds is measured now, against the oldest event each consumer
    /// has not yet applied. A consumer that has not run a pass yet is
    /// <see cref="ConsumerState.Behind"/> with unknown lag.</summary>
    public IReadOnlyList<ConsumerStatus> Status()
    {
        List<Registration> snapshot;
        lock (_consumersLock)
            snapshot = [.. _consumers];

        var now = _time.GetUtcNow();
        var result = new List<ConsumerStatus>(snapshot.Count);
        foreach (var (consumer, _) in snapshot)
        {
            if (!_progress.TryGetValue(consumer.Name, out var p))
            {
                result.Add(new ConsumerStatus(consumer.Name, consumer.IsReadModel, ConsumerState.Behind, null, null, null));
                continue;
            }
            var lagSeconds = p.PendingSince is { } since ? Math.Max(0, (now - since).TotalSeconds) : 0;
            var state = p.Blocked ? ConsumerState.Blocked
                : lagSeconds <= LagThreshold.TotalSeconds ? ConsumerState.Current
                : ConsumerState.Behind;
            long? lagPositions = p.Head is { } head ? Math.Max(0, head - p.Checkpoint) : null;
            result.Add(new ConsumerStatus(consumer.Name, consumer.IsReadModel, state, p.Checkpoint, lagPositions, lagSeconds));
        }
        return result.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The registered consumer names, sorted (a snapshot).</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_consumersLock)
                return _consumers.Select(r => r.Consumer.Name).Order().ToList();
        }
    }

    /// <summary>Runs the catch-up loop until <paramref name="ct"/> is cancelled: immediately
    /// on every committed event (in-process nudge) and on a slow tick fallback (covers
    /// restarts and missed nudges). Returns the background task; do not await it to
    /// completion except as part of shutdown; <see cref="StopAsync"/> is the orderly way to end it,
    /// and cancelling <paramref name="ct"/> the abrupt one.</summary>
    public Task StartAsync(CancellationToken ct)
    {
        _source.Subscribe(_ => _nudge.Writer.TryWrite(0));
        _hardStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = _hardStop.Token;

        return _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && !_stopRequested)
            {
                try
                {
                    await RunOnceAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (AggregateException)
                {
                    // RunOnceAsync has already logged each failing consumer, with its
                    // position; logging the aggregate again would double every line.
                }
                catch (Exception ex)
                {
                    _log($"consumer run error: {ex}");
                }

                try
                {
                    var nudged = _nudge.Reader.WaitToReadAsync(ct).AsTask();
                    var ticked = Task.Delay(_tick, ct);
                    await Task.WhenAny(nudged, ticked);
                    if (nudged.IsCompletedSuccessfully)
                        _nudge.Reader.TryRead(out _);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, ct);
    }

    /// <summary>Stops the engine in order (health/telemetry machine 1, draining): no new pass begins,
    /// and each consumer finishes the event it is applying, saves that checkpoint, and stops, so a
    /// restart resumes from the next event. It does not catch up first: that is what the restart
    /// is for. Returns true once every consumer has stopped. If <paramref name="deadline"/> passes
    /// first it cancels them mid-event (each event is applied at least once, so the interrupted
    /// one is redone on restart) and returns false. Returns true at once if the engine never
    /// started. After this the engine does not run again.</summary>
    public async Task<bool> StopAsync(TimeSpan deadline)
    {
        _stopRequested = true;
        _nudge.Writer.TryWrite(0);
        if (_loop is not { } loop)
            return true;

        var graceful = true;
        try
        {
            await loop.WaitAsync(deadline > TimeSpan.Zero ? deadline : TimeSpan.Zero);
        }
        catch (TimeoutException)
        {
            graceful = false;
            _log($"drain deadline of {deadline.TotalSeconds:0.###}s reached: cancelling consumers mid-event");
            _hardStop?.Cancel();
            try
            {
                // A consumer that ignores cancellation cannot be stopped from here; do not hang on it.
                await loop.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }
        catch (OperationCanceledException)
        {
            // The token given to StartAsync was cancelled first: already stopped, abruptly.
        }
        _hardStop?.Dispose();
        _hardStop = null;
        return graceful;
    }

    /// <summary>Applies every pending event to every consumer until caught up. A failing
    /// consumer stops at the failing event and retries next pass; other consumers are
    /// unaffected. The consumer set is snapshotted first, so a Register/Unregister swap
    /// applies cleanly to the next pass. Throws an <see cref="AggregateException"/>
    /// covering every consumer that failed this pass, or returns normally if all
    /// succeeded.
    ///
    /// <para>Passes take turns: a call made while another pass is running (the background loop's,
    /// or another caller's) waits for it, then runs its own, so it returns only once everything
    /// committed before the call has been applied. The wait honours <paramref name="ct"/>. A consumer
    /// must not call this from inside <c>ApplyAsync</c>: that pass would wait for itself.</para></summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await _passGate.WaitAsync(ct);
        try
        {
            await RunPassAsync(ct);
        }
        finally
        {
            _passGate.Release();
        }
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        List<Registration> snapshot;
        lock (_consumersLock)
            snapshot = [.. _consumers];

        // Read once per pass, for each consumer's lag in positions. A failure here is the store
        // being unreachable, which every consumer is about to report as blocked anyway.
        long? head = null;
        try
        {
            head = await _source.HeadPositionAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
        }

        List<Exception>? errors = null;
        foreach (var (consumer, checkpoints) in snapshot)
        {
            if (_stopRequested)
                break;
            if (await RunOnceForAsync(consumer, checkpoints, head, ct) is { } blocked)
                (errors ??= []).Add(blocked);
        }
        if (errors is { Count: > 0 })
            throw new AggregateException(errors);
    }

    /// <summary>Catches one consumer up, recording its progress for <see cref="Status"/> as it
    /// goes. Returns null when it caught up, or the (already logged) failure that blocked it;
    /// cancellation propagates.</summary>
    private async Task<Exception?> RunOnceForAsync(IConsumer consumer, ICheckpointStore checkpoints, long? head, CancellationToken ct)
    {
        long pos = 0;
        Event? current = null;
        _progress.TryGetValue(consumer.Name, out var before);
        // A blocked consumer stays blocked while it retries, until an event applies.
        var blocked = before?.Blocked ?? false;
        var pendingSince = before?.PendingSince;
        try
        {
            pos = await checkpoints.CheckpointAsync(consumer.Name, ct);
            while (true)
            {
                var batch = await _source.PollAsync(pos, 100, ct);
                if (batch.Count == 0) break;
                foreach (var ev in batch)
                {
                    current = ev;
                    // The event about to be applied is the oldest one not yet applied.
                    pendingSince = CreatedAt(ev);
                    _progress[consumer.Name] = new Progress(pos, head, pendingSince, blocked);
                    await consumer.ApplyAsync(ev, ct);
                    await checkpoints.SaveCheckpointAsync(consumer.Name, ev.Position, ct);
                    pos = ev.Position;
                    current = null;
                    blocked = false;
                    if (_stopRequested)
                    {
                        // Draining: the event in hand is applied and checkpointed; leave the rest.
                        _progress[consumer.Name] = new Progress(pos, head, pendingSince, false);
                        return null;
                    }
                }
            }
            _progress[consumer.Name] = new Progress(pos, head, null, false);
            return null;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // Only our own shutdown propagates as cancellation. An OperationCanceledException
            // the consumer raised itself (an HttpClient timeout, say) is a failure like any
            // other: letting it escape would end StartAsync's loop and silently stop every
            // consumer.
            //
            // The consumer is now blocked: its checkpoint stays put and the next pass
            // retries from the same event, indefinitely. Say which consumer and where,
            // since that is exactly what an operator needs to unstick it.
            var at = current is null
                ? $"position={pos} (reading checkpoint or polling after it)"
                : $"position={current.Position} event={current.Id} type={current.Type} stream={current.Aggregate}/{current.AggregateId}";
            _log($"consumer blocked, will retry: consumer={consumer.Name} {at} error={ex}");
            _progress[consumer.Name] = new Progress(pos, head, pendingSince ?? _time.GetUtcNow(), true);
            var where = current is null ? $"after position {pos}" : $"at position {current.Position}";
            return new InvalidOperationException($"consumer {consumer.Name} blocked {where}: {ex.Message}", ex);
        }
    }

    /// <summary>When <paramref name="ev"/> was committed. A timestamp that does not parse (a
    /// third-party store's own format) counts from now, so its lag still grows while it waits.</summary>
    private DateTimeOffset CreatedAt(Event ev) =>
        DateTimeOffset.TryParse(ev.Created, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : _time.GetUtcNow();

    private sealed record Registration(IConsumer Consumer, ICheckpointStore Checkpoints);

    /// <summary>A consumer's progress as of its latest pass: its checkpoint, the log head that
    /// pass saw, when the oldest event it has not applied was committed (null: caught up), and
    /// whether it is blocked.</summary>
    private sealed record Progress(long Checkpoint, long? Head, DateTimeOffset? PendingSince, bool Blocked);
}

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

    private readonly Lock _consumersLock = new();
    private readonly List<Registration> _consumers = [];

    // Bounded to 1 and drops on a full channel: the same "non-blocking nudge,
    // coalesce bursts" shape as pocketcqrs's buffered-channel-with-default-case.
    private readonly Channel<byte> _nudge = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Creates an engine that polls <paramref name="source"/> and checkpoints
    /// against <paramref name="checkpoints"/> — pass the same <see cref="SqliteEventStore"/>
    /// for both in the ordinary single-node case. <paramref name="tick"/> is the fallback
    /// poll interval (default 1s); <paramref name="logger"/> receives run-error messages
    /// (default: discarded).</summary>
    public ConsumerEngine(IPollSource source, ICheckpointStore checkpoints, TimeSpan? tick = null, Action<string>? logger = null)
    {
        _source = source;
        _checkpoints = checkpoints;
        _tick = tick ?? TimeSpan.FromSeconds(1);
        _log = logger ?? (_ => { });
    }

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
    /// completion except as part of shutdown.</summary>
    public Task StartAsync(CancellationToken ct)
    {
        _source.Subscribe(_ => _nudge.Writer.TryWrite(0));

        return Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
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

    /// <summary>Applies every pending event to every consumer until caught up. A failing
    /// consumer stops at the failing event and retries next pass; other consumers are
    /// unaffected. The consumer set is snapshotted first, so a Register/Unregister swap
    /// applies cleanly to the next pass. Throws an <see cref="AggregateException"/>
    /// covering every consumer that failed this pass, or returns normally if all
    /// succeeded.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        List<Registration> snapshot;
        lock (_consumersLock)
            snapshot = [.. _consumers];

        List<Exception>? errors = null;
        foreach (var (consumer, checkpoints) in snapshot)
        {
            if (await RunOnceForAsync(consumer, checkpoints, ct) is { } blocked)
                (errors ??= []).Add(blocked);
        }
        if (errors is { Count: > 0 })
            throw new AggregateException(errors);
    }

    /// <summary>Catches one consumer up. Returns null when it caught up, or the (already
    /// logged) failure that blocked it; cancellation propagates.</summary>
    private async Task<Exception?> RunOnceForAsync(IConsumer consumer, ICheckpointStore checkpoints, CancellationToken ct)
    {
        long pos = 0;
        Event? current = null;
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
                    await consumer.ApplyAsync(ev, ct);
                    await checkpoints.SaveCheckpointAsync(consumer.Name, ev.Position, ct);
                    pos = ev.Position;
                    current = null;
                }
            }
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
            var where = current is null ? $"after position {pos}" : $"at position {current.Position}";
            return new InvalidOperationException($"consumer {consumer.Name} blocked {where}: {ex.Message}", ex);
        }
    }

    private sealed record Registration(IConsumer Consumer, ICheckpointStore Checkpoints);
}

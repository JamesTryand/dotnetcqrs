using DotnetCqrs.Consumers;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Tests;

/// <summary>
/// dotnetcqrs-multi-node Milestone 6 acceptance: the whole write + read path
/// (<see cref="DeciderRegistry"/> deciding and appending, <see cref="ConsumerEngine"/>
/// polling and checkpointing, a consumer applying) runs end to end against a
/// hand-rolled in-memory <see cref="IEventStore"/> — there is no SQLite anywhere in
/// this file. That is the standing evidence that the abstraction really is
/// provider-neutral, so Milestone 7's Npgsql backend has a seam to slot into rather
/// than a SQLite-shaped hole. "104 tests still pass" proves nothing broke; this
/// proves the interface abstracts.
/// </summary>
public class AbstractionOnlyEndToEndTests
{
    /// <summary>A <see cref="List{Event}"/>-backed <see cref="IEventStore"/>: append is
    /// the commit, a per-stream count gives optimistic concurrency, insertion order is
    /// the global position. No persistence, no provider.</summary>
    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Lock _gate = new();
        private readonly List<Event> _events = [];
        private readonly Dictionary<string, long> _checkpoints = [];
        private readonly List<Action<Event>> _subscribers = [];

        public Task<IReadOnlyList<Event>> AppendAsync(
            string aggregate, string aggregateId, long expectedSequence, IReadOnlyList<NewEvent> events, CancellationToken ct = default)
        {
            List<Event> appended;
            lock (_gate)
            {
                var current = _events.Count(e => e.Aggregate == aggregate && e.AggregateId == aggregateId);
                if (current != expectedSequence)
                    throw new ConcurrencyException(aggregate, aggregateId, current, expectedSequence);

                appended = [];
                var sequence = current;
                foreach (var newEvent in events)
                {
                    sequence++;
                    var ev = new Event(
                        _events.Count + 1, Guid.NewGuid().ToString("N"), aggregate, aggregateId, sequence,
                        newEvent.Type, newEvent.Data, string.IsNullOrEmpty(newEvent.Metadata) ? "{}" : newEvent.Metadata,
                        "1970-01-01T00:00:00.000Z");
                    _events.Add(ev);
                    appended.Add(ev);
                }
            }
            foreach (var ev in appended)
                foreach (var subscriber in Subscribers())
                    subscriber(ev);
            return Task.FromResult<IReadOnlyList<Event>>(appended);
        }

        public Task<IReadOnlyList<Event>> LoadStreamAsync(string aggregate, string aggregateId, CancellationToken ct = default)
        {
            lock (_gate)
                return Task.FromResult<IReadOnlyList<Event>>(
                    _events.Where(e => e.Aggregate == aggregate && e.AggregateId == aggregateId).OrderBy(e => e.Sequence).ToList());
        }

        public Task<IReadOnlyList<Event>> PollAsync(long after, int limit, CancellationToken ct = default)
        {
            lock (_gate)
                return Task.FromResult<IReadOnlyList<Event>>(
                    _events.Where(e => e.Position > after).OrderBy(e => e.Position).Take(limit).ToList());
        }

        public void Subscribe(Action<Event> handler)
        {
            lock (_gate) _subscribers.Add(handler);
        }

        public Task<long> CheckpointAsync(string name, CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult(_checkpoints.GetValueOrDefault(name));
        }

        public Task SaveCheckpointAsync(string name, long position, CancellationToken ct = default)
        {
            lock (_gate) _checkpoints[name] = position;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private Action<Event>[] Subscribers()
        {
            lock (_gate) return [.. _subscribers];
        }
    }

    /// <summary>Records the (type, id) of every event it is handed.</summary>
    private sealed class RecordingConsumer : IConsumer
    {
        public List<string> Seen { get; } = [];
        public string Name => "recorder";

        public Task ApplyAsync(Event ev, CancellationToken ct)
        {
            Seen.Add($"{ev.Type}:{ev.AggregateId}");
            return Task.CompletedTask;
        }
    }

    private static Decider<int> CounterDecider() => new()
    {
        InitialState = () => 0,
        Decide = (count, cmd) => cmd.Name switch
        {
            "Increment" => [new NewEvent("Incremented", "{}")],
            "Reset" when count == 0 => throw new InvalidOperationException("counter is already at zero"),
            "Reset" => [new NewEvent("Reset", "{}")],
            _ => throw new InvalidOperationException($"unknown command: {cmd.Name}"),
        },
        Evolve = (count, ev) => ev.Type switch
        {
            "Incremented" => count + 1,
            "Reset" => 0,
            _ => count,
        },
    };

    [Fact]
    public async Task Decide_append_poll_and_checkpoint_run_end_to_end_with_no_sqlite()
    {
        await using var store = new InMemoryEventStore();

        var registry = new DeciderRegistry(store);
        registry.Register("counter", CounterDecider());

        var recorder = new RecordingConsumer();
        var engine = new ConsumerEngine(store, store);
        engine.Register(recorder);

        await registry.HandleAsync("counter", "c1", new Command("Increment", "{}"));
        await registry.HandleAsync("counter", "c1", new Command("Increment", "{}"));
        await registry.HandleAsync("counter", "c1", new Command("Reset", "{}"));

        await engine.RunOnceAsync();
        Assert.Equal(["Incremented:c1", "Incremented:c1", "Reset:c1"], recorder.Seen);

        // The checkpoint advanced, so a second pass replays nothing.
        recorder.Seen.Clear();
        await engine.RunOnceAsync();
        Assert.Empty(recorder.Seen);
    }

    [Fact]
    public async Task Optimistic_concurrency_and_domain_rejection_are_enforced_through_the_abstraction()
    {
        await using var store = new InMemoryEventStore();
        var registry = new DeciderRegistry(store);
        registry.Register("counter", CounterDecider());

        // A stale expected-sequence is rejected by the store contract, not by SQLite.
        await store.AppendAsync("counter", "c1", 0, [new NewEvent("Incremented", "{}")]);
        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.AppendAsync("counter", "c1", 0, [new NewEvent("Incremented", "{}")]));

        // A decider refusal still surfaces as the decider's own exception.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.HandleAsync("counter", "fresh", new Command("Reset", "{}")));
    }
}

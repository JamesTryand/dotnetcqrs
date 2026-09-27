using DotnetCqrs.Consumers;
using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Reactors;

/// <summary>
/// Adapts an <see cref="IReactor"/> to <see cref="IConsumer"/>: dispatches its
/// reactions through a <see cref="DeciderRegistry"/> in-process, so reactions become
/// events like everything else rather than an out-of-band state change. Checkpointed
/// as "reactor:&lt;name&gt;", sharing the same <see cref="ConsumerEngine"/> projections use.
/// </summary>
public sealed class ReactorConsumer(IReactor reactor, DeciderRegistry registry, Action<string>? logger = null) : IConsumer
{
    private readonly Action<string> _log = logger ?? (_ => { });

    public string Name => $"reactor:{reactor.Name}";

    public async Task ApplyAsync(Event ev, CancellationToken ct)
    {
        foreach (var reaction in reactor.React(ev))
        {
            var meta = new Dictionary<string, object>
            {
                ["actor"] = Name,
                ["causationId"] = ev.Id,
                ["correlationId"] = EventMeta.CorrelationId(ev),
            };
            try
            {
                await registry.HandleWithMetaAsync(reaction.Aggregate, reaction.Id, reaction.Command, meta, ct);
                _log($"reaction dispatched: reactor={reactor.Name} cause={ev.Id} " +
                     $"target={reaction.Aggregate}/{reaction.Id} command={reaction.Command.Name}");
            }
            catch (ConcurrencyException)
            {
                // the target stream moved between load and append; stop and retry
                // the whole event next pass, same as pocketcqrs's Dispatch.
                throw;
            }
            catch (UnknownAggregateException ex)
            {
                // Permanent wiring fault, not a domain refusal: no redelivery can
                // ever succeed. Log-and-continue rather than throw — blocking the
                // log on a fault that will never clear would stop every other
                // reaction too.
                _log($"reaction dropped: target aggregate not registered: reactor={reactor.Name} " +
                     $"cause={ev.Id} target={reaction.Aggregate}/{reaction.Id} error={ex.Message}");
            }
            catch (Exception ex) when (DeciderRegistry.IsRejection(ex))
            {
                // Domain rejection, including the idempotency path (e.g. "already
                // exists" on redelivery): log and continue, never block the log.
                //
                // Anything else propagates: a dependency failure (event store, key
                // service, a timeout, shutdown) or a bug around the decision means the
                // command was never decided on its merits. Dropping it would advance the
                // checkpoint and lose the reaction for good; instead the consumer blocks
                // and retries the whole event next pass, as a projection does, and the
                // engine logs the consumer, position and error. Reactions already
                // dispatched for this event are dispatched again on retry and rely on the
                // target decider's own idempotency, exactly as the ConcurrencyException
                // path above does.
                _log($"reaction rejected: reactor={reactor.Name} cause={ev.Id} " +
                     $"target={reaction.Aggregate}/{reaction.Id} error={ex.Message}");
            }
        }
    }
}

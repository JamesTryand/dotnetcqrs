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
            try
            {
                await registry.HandleAsync(reaction.Aggregate, reaction.Id, reaction.Command, ct);
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
            catch (Exception ex)
            {
                // Domain rejection, including the idempotency path (e.g. "already
                // exists" on redelivery): log and continue, never block the log.
                _log($"reaction rejected: reactor={reactor.Name} cause={ev.Id} " +
                     $"target={reaction.Aggregate}/{reaction.Id} error={ex.Message}");
            }
        }
    }
}

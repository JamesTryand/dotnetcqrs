using DotnetCqrs.Deciders;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Reactors;

/// <summary>A command to dispatch in response to an event.</summary>
public sealed record Reaction(string Aggregate, string Id, Command Command);

/// <summary>
/// Maps committed events to reactions — cross-aggregate reactions (the "saga"
/// pattern): "TaskCompleted" -&gt; reactor -&gt; "CreateNote" command -&gt; decider decides
/// -&gt; "NoteCreated" event, per the concepts doc's "Reactors" section.
/// </summary>
public interface IReactor
{
    /// <summary>The durable checkpoint key.</summary>
    string Name { get; }

    /// <summary>Maps one committed event to zero or more reactions. Delivery is
    /// at-least-once, so a target aggregate id should be deterministically derived
    /// from the source event (e.g. "fulfill-&lt;orderId&gt;") — a replay then hits the
    /// target decider's own "already exists" rejection, which <see cref="ReactorConsumer"/>
    /// logs and skips.</summary>
    IReadOnlyList<Reaction> React(Event ev);
}

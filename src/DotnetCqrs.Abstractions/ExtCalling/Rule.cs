using DotnetCqrs.EventStore;

namespace DotnetCqrs.ExtCalling;

/// <summary>One follow-up command a <see cref="Rule"/>'s <see cref="Rule.HandleResponse"/>
/// wants dispatched. <see cref="ExtCallerConsumer"/> derives its own deterministic
/// causation/correlation metadata from the source event and this follow-up's position —
/// rule authors never invent one themselves.</summary>
public sealed record FollowUp(string Aggregate, string Id, string Name, string Payload);

/// <summary>
/// Maps one event type to an outbound HTTP request and maps that request's response to
/// zero or more follow-up commands. Deliberately plain delegates, not a declarative
/// mapping DSL — a Rule is assembled by whoever wires up a deployment, same reasoning
/// as pocketcqrs's own <c>extcaller.Rule</c>.
/// </summary>
public sealed class Rule
{
    /// <summary>Matched against <see cref="Event.Type"/> exactly.</summary>
    public required string EventType { get; init; }

    /// <summary>Turns the causing event into the outbound request to make. Called fresh
    /// on every retry attempt, so it must return a usable, not-yet-sent
    /// <see cref="HttpRequestMessage"/> each time.</summary>
    public required Func<Event, HttpRequestMessage> BuildRequest { get; init; }

    /// <summary>Turns a successful response into zero or more follow-up commands. A
    /// thrown exception is treated as a permanent failure for this event (dead-lettered),
    /// same as a <see cref="BuildRequest"/> or outbound-call failure.</summary>
    public required Func<Event, HttpResponseMessage, Task<IReadOnlyList<FollowUp>>> HandleResponse { get; init; }
}

/// <summary>Bounds how many times <see cref="ExtCallerConsumer"/> calls out for one
/// event before giving up and dead-lettering. <see cref="MaxAttempts"/> &lt;= 0 is
/// treated as 1 (no retry).</summary>
public sealed record RetryPolicy(int MaxAttempts = 1, TimeSpan Backoff = default);

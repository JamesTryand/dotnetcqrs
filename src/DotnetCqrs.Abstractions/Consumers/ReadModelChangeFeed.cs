namespace DotnetCqrs.Consumers;

/// <summary>A read model's table changed: a projection applied events to it, up to and including
/// <paramref name="Position"/>, and recorded that in its checkpoint.</summary>
public sealed record ReadModelChanged(string Table, long Position);

/// <summary>
/// The last step of the data flow, for views that are on screen right now: command → decider → events →
/// projection applies → <b>read model changed</b> → whoever is showing that view updates. A live view
/// subscribes for as long as it is shown, and is called back when a projection changes one of the tables it
/// depends on. Nothing polls and nothing waits; the notification follows the projection, so it arrives with
/// whatever latency the flow naturally has.
///
/// <para>Notifications are coalesced: a projection that applies a batch of events publishes one change per
/// table it owns for the whole batch, carrying the batch's last position. Only projections publish (an
/// automation changes no view). A change is published only after the projection's checkpoint is saved, so it is
/// never ahead of what a reader can see.</para>
///
/// <para><b>Callbacks run on the engine's loop and must not block:</b> hand the notification to your own queue
/// (a channel, say) and return. A callback that throws is logged and skipped; it never stops the engine or the
/// other subscribers. The feed is in-process: other nodes learn of events through their own engines.</para>
/// </summary>
public interface IReadModelChangeFeed
{
    /// <summary>Calls <paramref name="onChanged"/> whenever a projection changes one of
    /// <paramref name="tables"/>, until the returned subscription is disposed.</summary>
    IDisposable Subscribe(IReadOnlyCollection<string> tables, Action<ReadModelChanged> onChanged);
}

/// <summary>A consumer that is not a projection, but whose work still changes what a view shows. Erasing someone
/// changes no table, yet once their key is gone (or this process has marked them erased) their personal data
/// reads back redacted, so every view holding it has changed. After each batch the engine asks which tables
/// changed and tells their live subscribers, as it does for a projection's own tables.</summary>
public interface IChangesViews : IConsumer
{
    /// <summary>The tables whose result changed since the last call (empty when none did), and resets.</summary>
    IReadOnlyCollection<string> TakeChangedTables();
}

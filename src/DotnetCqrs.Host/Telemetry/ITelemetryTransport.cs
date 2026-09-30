namespace DotnetCqrs.Host.Telemetry;

/// <summary>
/// Where the telemetry push (health/telemetry contract section 8) sends a snapshot. The push builds
/// the payload and decides when to send; a transport only delivers bytes to its bus, so NATS is one
/// implementation and Kafka or RabbitMQ can be others without the push changing.
///
/// <para>The contract binds one logical shape, topic <c>cqrs.telemetry.metrics</c> with key
/// <c>&lt;node_id&gt;</c>, to each transport (NATS: subject <c>cqrs.telemetry.metrics.&lt;node_id&gt;</c>;
/// Kafka: that topic, keyed; RabbitMQ: a topic exchange of that name, routing key the node id), and
/// the transport applies its own binding.</para>
///
/// <para>Best-effort by contract: <see cref="PublishAsync"/> must not queue what it cannot send. If
/// the bus is unreachable it throws, and the push drops that snapshot. Connecting and reconnecting
/// happen in the background, never on the caller's time.</para>
/// </summary>
public interface ITelemetryTransport : IAsyncDisposable
{
    /// <summary>Delivers one snapshot for the node <paramref name="key"/> (its <c>node_id</c>).
    /// Throws when it could not, or when <paramref name="ct"/> is cancelled first.</summary>
    ValueTask PublishAsync(string key, ReadOnlyMemory<byte> payload, CancellationToken ct);
}

/// <summary>Builds a transport for a <c>CQRS_TELEMETRY_URL</c>. <c>log</c> hears connection events.</summary>
public delegate ITelemetryTransport TelemetryTransportFactory(Uri url, Action<string> log);

/// <summary>The transports this host can push through, by URL scheme. Nothing is registered by
/// default: the host registers what it ships (see <c>NatsTelemetry.Register</c>), so no transport
/// is hard-wired into the push.</summary>
public sealed class TelemetryTransports
{
    private readonly Dictionary<string, TelemetryTransportFactory> _byScheme = new(StringComparer.OrdinalIgnoreCase);

    public TelemetryTransports Register(string scheme, TelemetryTransportFactory factory)
    {
        _byScheme[scheme] = factory;
        return this;
    }

    public IReadOnlyCollection<string> Schemes => _byScheme.Keys;

    /// <summary>The transport for <paramref name="url"/>'s scheme. Throws
    /// <see cref="InvalidTelemetrySettingException"/> for a scheme this host has no transport for: a
    /// misconfiguration stops the node from starting, an unreachable bus does not.</summary>
    public ITelemetryTransport Create(Uri url, Action<string> log) =>
        _byScheme.TryGetValue(url.Scheme, out var factory)
            ? factory(url, log)
            : throw new InvalidTelemetrySettingException(TelemetrySettings.UrlVariable,
                $"the scheme '{url.Scheme}' has no telemetry transport in this host (available: "
                + (_byScheme.Count == 0 ? "none" : string.Join(", ", _byScheme.Keys.Order(StringComparer.Ordinal))) + ").");
}

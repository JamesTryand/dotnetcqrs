using DotnetCqrs.Host.Telemetry;
using NATS.Client.Core;

namespace DotnetCqrs.Host.Telemetry.Nats;

/// <summary>
/// The NATS binding of the telemetry push (health/telemetry contract section 8.2): each snapshot is
/// published to the subject <c>cqrs.telemetry.metrics.&lt;node_id&gt;</c>. <c>node_id</c> is one
/// literal subject token by construction (node-identity 1.2).
///
/// <para>Best-effort: while the connection is not open a publish throws instead of queueing, so a
/// snapshot is never delivered late, and the client reconnects in the background (NATS.Net retries
/// without limit). A URL may carry credentials, <c>nats://user:pass@host:4222</c>, and is never
/// logged.</para>
/// </summary>
public sealed class NatsTelemetryTransport : ITelemetryTransport
{
    /// <summary>The URL scheme that selects this transport.</summary>
    public const string Scheme = "nats";

    /// <summary>The subject prefix; the node id is the last token.</summary>
    public const string SubjectPrefix = "cqrs.telemetry.metrics.";

    private readonly NatsConnection _connection;
    private readonly Action<string> _log;
    private int _connecting;

    public NatsTelemetryTransport(Uri url, Action<string> log)
    {
        _log = log;
        _connection = new NatsConnection(NatsOpts.Default with
        {
            Url = url.ToString(),
            Name = "dotnetcqrs-telemetry",
            ConnectTimeout = TimeSpan.FromSeconds(2),
        });
        _ = ConnectInBackgroundAsync();
    }

    /// <summary>Registers this transport for <c>nats://</c> URLs.</summary>
    public static TelemetryTransports Register(TelemetryTransports transports) =>
        transports.Register(Scheme, (url, log) => new NatsTelemetryTransport(url, log));

    public async ValueTask PublishAsync(string key, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (_connection.ConnectionState != NatsConnectionState.Open)
        {
            _ = ConnectInBackgroundAsync();
            throw new IOException("not connected to NATS");
        }
        await _connection.PublishAsync(SubjectPrefix + key, payload.ToArray(), cancellationToken: ct);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private async Task ConnectInBackgroundAsync()
    {
        if (_connection.ConnectionState != NatsConnectionState.Closed || Interlocked.Exchange(ref _connecting, 1) == 1)
            return;
        try
        {
            await _connection.ConnectAsync();
        }
        catch (Exception ex)
        {
            _log($"telemetry: could not connect to NATS yet ({ex.GetType().Name}); will keep trying");
        }
        finally
        {
            Interlocked.Exchange(ref _connecting, 0);
        }
    }
}

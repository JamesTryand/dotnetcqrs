using NATS.Client.Core;

namespace DotnetCqrs.Edges.Nats;

/// <summary>The connection a host carries its edges on. The URL and credentials come from the host's own
/// configuration (surface tooling's), never from the coordinating document.</summary>
public static class EdgesNatsConnection
{
    /// <summary>A connection that keeps trying while the server is unreachable, at start as well as later, so a
    /// host whose carrier is down still starts and serves, and carries its edges once the carrier is back.</summary>
    public static NatsConnection Open(string url, string? user, string? password, string name) =>
        new(NatsOpts.Default with
        {
            Url = url,
            Name = name,
            AuthOpts = string.IsNullOrEmpty(user) ? NatsAuthOpts.Default : NatsAuthOpts.Default with { Username = user, Password = password },
            RetryOnInitialConnect = true,
            MaxReconnectRetry = -1,
        });
}

using System.Data.Common;
using System.Net.Sockets;
using DotnetCqrs.EventStore;

namespace DotnetCqrs.Deciders;

/// <summary>Marks an exception type as "a dependency failed" -- the command was never
/// refused on its merits. Implement it on a client library's own failure type (as
/// <c>DotnetCqrs.Crypto</c>'s <c>KmsProtocolException</c> does) so the dispatch shell
/// treats it like a database or network error without referencing that library.</summary>
public interface IInfrastructureFailure;

/// <summary>
/// Tells an infrastructure failure apart from a decider's refusal, for the dispatch
/// shell (the gateway's status codes, a reactor's retry-or-drop), without touching the
/// decider contract. <c>Decide</c> is pure and synchronous: it does no I/O and takes no
/// cancellation token, so a database, network, key-service, read-only-store, timeout or
/// cancellation failure cannot be one of its rejections -- it came from the shell around
/// it (loading the stream, revealing or protecting PII, appending).
///
/// <para>This is a deny-list, not a positive test for a domain rejection: <c>Decide</c>
/// rejects by throwing any exception it likes, so anything not recognised here is still
/// treated as a rejection. The inner-exception chain is searched too, since a store or
/// client may wrap its driver's error.</para>
/// </summary>
public static class InfrastructureFailure
{
    /// <summary>True when <paramref name="ex"/> (or anything it wraps) is a dependency
    /// failure rather than a decision: <see cref="DbException"/> (SQLite, Npgsql),
    /// <see cref="HttpRequestException"/>, <see cref="SocketException"/>,
    /// <see cref="IOException"/>, <see cref="TimeoutException"/>,
    /// <see cref="OperationCanceledException"/>, <see cref="ReadOnlyStoreException"/>, or
    /// any <see cref="IInfrastructureFailure"/>.</summary>
    public static bool IsInfrastructure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate)
                return aggregate.InnerExceptions.Any(IsInfrastructure);
            if (current is IInfrastructureFailure or DbException or HttpRequestException or SocketException
                or IOException or TimeoutException or OperationCanceledException or ReadOnlyStoreException)
                return true;
        }
        return false;
    }
}

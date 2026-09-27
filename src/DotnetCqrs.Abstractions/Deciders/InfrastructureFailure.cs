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
/// Recognises a failed dependency among the failures around a decision, so the gateway
/// can answer 503 (retry later) rather than 500 (host fault). Whether an exception is a
/// rejection at all is <see cref="DeciderRegistry.IsRejection"/>'s question, answered by
/// where it was thrown; this one only sorts what is left. <c>Decide</c> is pure and
/// synchronous -- no I/O, no cancellation token -- so none of these types can come from it.
///
/// <para>The inner-exception chain is searched too, since a store or client may wrap its
/// driver's error.</para>
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

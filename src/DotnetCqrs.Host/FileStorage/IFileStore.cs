namespace DotnetCqrs.Host.FileStorage;

/// <summary>
/// A pluggable object store, keyed by an opaque string: local disk to start,
/// swappable to S3/Azure Blob later behind the same interface. Deliberately
/// decoupled from the event-sourcing model entirely — no event, no aggregate
/// involvement — matching how pocketcqrs/PocketBase never integrated file storage
/// with CQRS either. A route handler calls this before/after a command; the key
/// (e.g. an attachment id) goes in the command payload like any other data.
/// </summary>
public interface IFileStore
{
    /// <summary>Writes <paramref name="content"/> under <paramref name="key"/>,
    /// overwriting any existing value.</summary>
    Task PutAsync(string key, Stream content, CancellationToken ct = default);

    /// <summary>Returns the content at <paramref name="key"/>, or <c>null</c> if it
    /// doesn't exist. The caller owns disposing the returned stream.</summary>
    Task<Stream?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Removes <paramref name="key"/>. A no-op if it doesn't exist.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
}

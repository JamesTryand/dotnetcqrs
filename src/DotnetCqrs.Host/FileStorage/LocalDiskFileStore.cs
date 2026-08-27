namespace DotnetCqrs.Host.FileStorage;

/// <summary>An <see cref="IFileStore"/> backed by the local filesystem, rooted at one
/// directory. The default/first implementation — swap for an S3/Azure Blob
/// implementation later without touching anything that depends on <see cref="IFileStore"/>.</summary>
public sealed class LocalDiskFileStore : IFileStore
{
    private readonly string _root;

    public LocalDiskFileStore(string rootDirectory)
    {
        _root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_root);
    }

    public async Task PutAsync(string key, Stream content, CancellationToken ct = default)
    {
        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
    }

    public Task<Stream?> GetAsync(string key, CancellationToken ct = default)
    {
        var path = ResolvePath(key);
        return Task.FromResult(File.Exists(path) ? (Stream)File.OpenRead(path) : null);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        File.Delete(ResolvePath(key)); // no-op if the file doesn't exist, matching File.Delete
        return Task.CompletedTask;
    }

    // key is untrusted input (ultimately from an HTTP caller), so this is a real
    // trust boundary: Path.Combine happily honors a rooted second argument
    // (silently discarding the root), and ".." segments must be resolved before
    // checking containment -- Path.GetFullPath does that, then
    // Path.GetRelativePath's own ".." prefix is the standard, correct way to
    // detect "resolved outside the root" for either reason.
    private string ResolvePath(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("key must not be empty", nameof(key));

        var full = Path.GetFullPath(Path.Combine(_root, key));
        var relative = Path.GetRelativePath(_root, full);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException($"key '{key}' escapes the file store root", nameof(key));

        return full;
    }
}

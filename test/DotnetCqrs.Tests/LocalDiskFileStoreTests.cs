using System.Text;
using DotnetCqrs.Host.FileStorage;

namespace DotnetCqrs.Tests;

public class LocalDiskFileStoreTests : IDisposable
{
    private readonly string _root;
    private readonly LocalDiskFileStore _store;

    public LocalDiskFileStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-filestore-{Guid.NewGuid():N}");
        _store = new LocalDiskFileStore(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static Stream ContentOf(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var _ = stream;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Put_then_Get_round_trips_the_content()
    {
        await _store.PutAsync("hello.txt", ContentOf("hello world"));

        var content = await _store.GetAsync("hello.txt");
        Assert.NotNull(content);
        Assert.Equal("hello world", await ReadAllAsync(content!));
    }

    [Fact]
    public async Task Get_returns_null_for_a_missing_key()
    {
        Assert.Null(await _store.GetAsync("nope.txt"));
    }

    [Fact]
    public async Task Put_overwrites_an_existing_key()
    {
        await _store.PutAsync("hello.txt", ContentOf("first"));
        await _store.PutAsync("hello.txt", ContentOf("second"));

        var content = await _store.GetAsync("hello.txt");
        Assert.Equal("second", await ReadAllAsync(content!));
    }

    [Fact]
    public async Task Delete_removes_the_key_and_is_a_no_op_when_already_missing()
    {
        await _store.PutAsync("hello.txt", ContentOf("x"));
        await _store.DeleteAsync("hello.txt");
        Assert.Null(await _store.GetAsync("hello.txt"));

        await _store.DeleteAsync("hello.txt"); // must not throw
    }

    [Fact]
    public async Task A_nested_key_creates_the_needed_subdirectories()
    {
        await _store.PutAsync("orders/o1/invoice.pdf", ContentOf("pdf-bytes"));

        var content = await _store.GetAsync("orders/o1/invoice.pdf");
        Assert.Equal("pdf-bytes", await ReadAllAsync(content!));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("../../outside.txt")]
    [InlineData("nested/../../outside.txt")]
    public async Task A_key_that_escapes_the_root_via_dot_dot_is_rejected(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.PutAsync(key, ContentOf("x")));

        // and it genuinely didn't write outside the root
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "outside.txt")));
    }

    [Fact]
    public async Task An_absolute_path_key_is_rejected_not_silently_honored()
    {
        // Path.Combine(root, absolutePath) discards `root` entirely -- this proves
        // ResolvePath's containment check catches that case too, not just "..".
        var elsewhere = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-filestore-elsewhere-{Guid.NewGuid():N}.txt");

        await Assert.ThrowsAsync<ArgumentException>(() => _store.PutAsync(elsewhere, ContentOf("x")));

        Assert.False(File.Exists(elsewhere));
    }

    [Fact]
    public async Task An_empty_key_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetAsync(""));
    }
}

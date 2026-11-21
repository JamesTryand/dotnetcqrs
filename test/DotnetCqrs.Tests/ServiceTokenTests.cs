using DotnetCqrs.Host;

namespace DotnetCqrs.Tests;

/// <summary>
/// The service-token credential (the gateway contract's G2): issued by the host with no outside identity provider,
/// kept only as a hash, one role per account, revocable while the host runs.
/// </summary>
public sealed class ServiceTokenTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dotnetcqrs-tokens-{Guid.NewGuid():N}");
    private string TokensPath => Path.Combine(_dir, "service-tokens.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void An_issued_token_authenticates_as_its_account_and_only_its_hash_is_stored()
    {
        var store = new ServiceTokenStore(TokensPath);
        var issued = store.Issue("payments", "payments");

        Assert.StartsWith(ServiceTokenDefaults.TokenPrefix, issued.Token);
        Assert.True(issued.Token.Length >= 40);
        Assert.Equal(new ServiceAccount("payments", "payments", issued.Account.Created), store.Authenticate(issued.Token));
        Assert.DoesNotContain(issued.Token, File.ReadAllText(TokensPath));
        Assert.DoesNotContain(issued.Token[ServiceTokenDefaults.TokenPrefix.Length..], File.ReadAllText(TokensPath));
    }

    [Fact]
    public void An_unknown_or_altered_token_authenticates_no_one()
    {
        var store = new ServiceTokenStore(TokensPath);
        Assert.Null(store.Authenticate("dcq_anything")); // no file yet
        var issued = store.Issue("payments", "payments");

        Assert.Null(store.Authenticate(issued.Token[..^1] + (issued.Token[^1] == 'A' ? 'B' : 'A')));
        Assert.Null(store.Authenticate(issued.Token[ServiceTokenDefaults.TokenPrefix.Length..]));
        Assert.Null(store.Authenticate(""));
    }

    [Fact]
    public void Two_tokens_are_never_the_same_and_a_name_is_issued_once()
    {
        var store = new ServiceTokenStore(TokensPath);
        var a = store.Issue("a", "role-a");
        var b = store.Issue("b", "role-b");

        Assert.NotEqual(a.Token, b.Token);
        Assert.Equal("role-b", store.Authenticate(b.Token)!.Role);
        Assert.Throws<InvalidOperationException>(() => store.Issue("a", "role-a"));
        Assert.Throws<ArgumentException>(() => store.Issue("has space", "r"));
        Assert.Throws<ArgumentException>(() => store.Issue("ok", ""));
    }

    [Fact]
    public void A_change_made_by_another_store_on_the_same_file_is_seen_at_once()
    {
        // The host's running store, and the --issue-token / --revoke-token command's own.
        var running = new ServiceTokenStore(TokensPath);
        var command = new ServiceTokenStore(TokensPath);

        var issued = command.Issue("scheduler", "ops");
        Assert.Equal("scheduler", running.Authenticate(issued.Token)!.Name);

        Assert.True(command.Revoke("scheduler"));
        Assert.Null(running.Authenticate(issued.Token));
        Assert.False(command.Revoke("scheduler"));
    }

    [Fact]
    public void The_host_command_prints_the_token_once_and_lists_accounts_without_it()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(0, ServiceTokenCommand.Run(["--issue-token", "payments", "--role", "payments"], TokensPath, output, error));
        var token = output.ToString().Trim();
        Assert.StartsWith(ServiceTokenDefaults.TokenPrefix, token);
        Assert.DoesNotContain(token, error.ToString());
        Assert.NotNull(new ServiceTokenStore(TokensPath).Authenticate(token));

        var listed = new StringWriter();
        Assert.Equal(0, ServiceTokenCommand.Run(["--list-tokens"], TokensPath, listed, error));
        Assert.StartsWith("payments\tpayments\t", listed.ToString());
        Assert.DoesNotContain(token, listed.ToString());

        Assert.Equal(1, ServiceTokenCommand.Run(["--issue-token", "payments", "--role", "payments"], TokensPath, new StringWriter(), error));
        Assert.Equal(2, ServiceTokenCommand.Run(["--issue-token", "payments"], TokensPath, new StringWriter(), error));
        Assert.Equal(0, ServiceTokenCommand.Run(["--revoke-token", "payments"], TokensPath, new StringWriter(), error));
        Assert.Null(new ServiceTokenStore(TokensPath).Authenticate(token));
        Assert.True(ServiceTokenCommand.Handles(["--list-tokens"]));
        Assert.False(ServiceTokenCommand.Handles(["--verify"]));
    }
}

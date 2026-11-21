using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetCqrs.Host;

/// <summary>Names shared by the service-token scheme.</summary>
public static class ServiceTokenDefaults
{
    /// <summary>The authentication scheme's name.</summary>
    public const string Scheme = "ServiceToken";

    /// <summary>Every token starts with this, so a secret scanner can spot one.</summary>
    public const string TokenPrefix = "dcq_";

    /// <summary>The environment variable a generated host reads the token file's path from.</summary>
    public const string PathVariable = "DOTNETCQRS_TOKENS_PATH";
}

/// <summary>A service account: a name (the actor its commands are stamped with) and one role.</summary>
public sealed record ServiceAccount(string Name, string Role, DateTimeOffset Created);

/// <summary>A token just issued. <see cref="Token"/> exists only here: the store keeps its hash.</summary>
public sealed record IssuedServiceToken(ServiceAccount Account, string Token);

/// <summary>
/// Service accounts for callers outside the node (a webhook receiver, a scheduler, another system), issued by the
/// host itself with no outside identity provider: the command gateway contract v0.1's G2 credential. Each account has
/// a name, one role and one long-lived bearer token. The file at <see cref="Path"/> holds each account's SHA-256 of
/// its token, never the token: a token is 256 random bits, so its hash cannot be reversed or guessed. Only hashes are
/// compared, in constant time. The file is re-read when it changes, so a token issued or revoked by
/// <see cref="ServiceTokenCommand"/> while the host runs takes effect at once.
/// </summary>
public sealed class ServiceTokenStore
{
    private static readonly Regex NamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex RolePattern = new("^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly TimeProvider _time;
    private readonly Lock _cacheLock = new();
    private (DateTime WrittenAt, long Length, IReadOnlyList<Entry> Entries)? _cache;

    /// <param name="path">The token file. It need not exist yet: with none, no token is valid.</param>
    /// <param name="time">The clock stamped on a new account; the system clock by default.</param>
    public ServiceTokenStore(string path, TimeProvider? time = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The token file's full path.</summary>
    public string Path { get; }

    /// <summary>The token file's path from <see cref="ServiceTokenDefaults.PathVariable"/>, else
    /// <paramref name="defaultPath"/>.</summary>
    public static string PathFromEnvironment(string defaultPath) =>
        Environment.GetEnvironmentVariable(ServiceTokenDefaults.PathVariable) is { Length: > 0 } configured ? configured : defaultPath;

    /// <summary>Issues a new account and its token. Refuses a name already issued (revoke it first, or issue
    /// under a new name and move callers over before revoking the old one).</summary>
    public IssuedServiceToken Issue(string name, string role)
    {
        if (!NamePattern.IsMatch(name))
            throw new ArgumentException($"a service account name is 1-64 letters, digits, '.', '_' or '-', not '{name}'", nameof(name));
        if (!RolePattern.IsMatch(role))
            throw new ArgumentException($"a role is 1-64 letters, digits, '.', '_', ':' or '-', not '{role}'", nameof(role));

        var token = ServiceTokenDefaults.TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var account = new ServiceAccount(name, role, _time.GetUtcNow());
        Update(entries =>
        {
            if (entries.Any(e => e.Name == name))
                throw new InvalidOperationException($"a token for '{name}' already exists; revoke it first");
            entries.Add(new Entry(name, role, Hash(token), account.Created));
        });
        return new IssuedServiceToken(account, token);
    }

    /// <summary>Revokes the account named <paramref name="name"/>; false when there is none.</summary>
    public bool Revoke(string name)
    {
        var removed = false;
        Update(entries => removed = entries.RemoveAll(e => e.Name == name) > 0);
        return removed;
    }

    /// <summary>Every account, without its hash.</summary>
    public IReadOnlyList<ServiceAccount> List() =>
        Load().Select(e => new ServiceAccount(e.Name, e.Role, e.Created)).ToList();

    /// <summary>The account <paramref name="token"/> was issued for, or null when it is unknown or revoked.</summary>
    public ServiceAccount? Authenticate(string token)
    {
        if (!token.StartsWith(ServiceTokenDefaults.TokenPrefix, StringComparison.Ordinal) || token.Length > 256)
            return null;
        var presented = Convert.FromHexString(Hash(token));
        ServiceAccount? match = null;
        // Every entry is compared, in constant time, whichever matches.
        foreach (var entry in Load())
            if (CryptographicOperations.FixedTimeEquals(presented, Convert.FromHexString(entry.TokenSha256)))
                match = new ServiceAccount(entry.Name, entry.Role, entry.Created);
        return match;
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private IReadOnlyList<Entry> Load()
    {
        var info = new FileInfo(Path);
        if (!info.Exists)
            return [];
        lock (_cacheLock)
        {
            if (_cache is { } cached && cached.WrittenAt == info.LastWriteTimeUtc && cached.Length == info.Length)
                return cached.Entries;
            var entries = Read();
            _cache = (info.LastWriteTimeUtc, info.Length, entries);
            return entries;
        }
    }

    private List<Entry> Read()
    {
        if (!File.Exists(Path))
            return [];
        var file = JsonSerializer.Deserialize<TokenFile>(File.ReadAllText(Path), JsonOptions)
            ?? throw new InvalidDataException($"the service token file {Path} is empty");
        return file.Accounts ?? [];
    }

    /// <summary>Read, change, write, under a lock file so two issuing commands never lose each other's change. The
    /// new file is written beside the old one (owner-only on Unix) and moved over it, so a reader never sees half.</summary>
    private void Update(Action<List<Entry>> change)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var _ = AcquireLock();
        var entries = Read();
        change(entries);
        var temp = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, new TokenFile(1, entries), JsonOptions);
            }
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        lock (_cacheLock) _cache = null;
    }

    private FileStream AcquireLock()
    {
        var lockPath = Path + ".lock";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed record Entry(string Name, string Role, string TokenSha256, DateTimeOffset Created);

    private sealed record TokenFile(int Version, List<Entry>? Accounts);
}

/// <summary>Options for <see cref="ServiceTokenHandler"/>.</summary>
public sealed class ServiceTokenOptions : AuthenticationSchemeOptions
{
    /// <summary>The accounts and their token hashes.</summary>
    public ServiceTokenStore? Store { get; set; }

    /// <summary>The claim type the account's role is issued under: whatever the host's authorization reads the role
    /// from (a generated host's <c>Cqrs:RoleClaim</c>, default <c>role</c>).</summary>
    public string RoleClaimType { get; set; } = "role";

    public override void Validate()
    {
        base.Validate();
        if (Store is null)
            throw new InvalidOperationException("ServiceTokenOptions.Store is required");
    }
}

/// <summary>
/// Authenticates <c>Authorization: Bearer &lt;token&gt;</c> against a <see cref="ServiceTokenStore"/>. The caller's
/// claims: the account's name as the name identifier and the name (so it is the actor stamped on its events), and its
/// role under <see cref="ServiceTokenOptions.RoleClaimType"/>. No credential, a non-bearer one, or an unknown or
/// revoked token is answered <c>401</c> with <c>WWW-Authenticate: Bearer</c> and a problem body; a forbidden one
/// <c>403</c> with a problem body. A token never appears in a log or an answer.
/// </summary>
public sealed class ServiceTokenHandler(IOptionsMonitor<ServiceTokenOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<ServiceTokenOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());
        var account = Options.Store!.Authenticate(header["Bearer ".Length..].Trim());
        if (account is null)
            return Task.FromResult(AuthenticateResult.Fail("unknown or revoked service token"));

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, account.Name),
            new Claim(ClaimTypes.Name, account.Name),
            new Claim(Options.RoleClaimType, account.Role),
        ], Scheme.Name, ClaimTypes.Name, Options.RoleClaimType);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        await Results.Problem("this route needs a service token: send Authorization: Bearer <token>, issued by the host",
            statusCode: StatusCodes.Status401Unauthorized, title: "unauthorized").ExecuteAsync(Context);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        await Results.Problem("this service account may not use this route",
            statusCode: StatusCodes.Status403Forbidden, title: "forbidden").ExecuteAsync(Context);
}

/// <summary>Registers <see cref="ServiceTokenHandler"/>.</summary>
public static class ServiceTokenExtensions
{
    /// <summary>Adds the service-token scheme over <paramref name="store"/>, issuing the role under
    /// <paramref name="roleClaimType"/>. Make it the default scheme (<c>AddAuthentication(ServiceTokenDefaults.Scheme)</c>)
    /// and chain <c>.RequireAuthorization()</c> on the gateway.</summary>
    public static AuthenticationBuilder AddServiceTokens(
        this AuthenticationBuilder builder, ServiceTokenStore store, string roleClaimType = "role",
        string scheme = ServiceTokenDefaults.Scheme) =>
        builder.AddScheme<ServiceTokenOptions, ServiceTokenHandler>(scheme, o =>
        {
            o.Store = store;
            o.RoleClaimType = roleClaimType;
        });
}

/// <summary>
/// The host's own commands for service accounts, run as <c>dotnet run -- --issue-token &lt;name&gt; --role
/// &lt;role&gt;</c>, <c>--revoke-token &lt;name&gt;</c> and <c>--list-tokens</c>. They touch only the token file, so they
/// run beside a live host (which sees the change at once) and need nothing else configured. An issued token is
/// written to standard output, once, and nowhere else.
/// </summary>
public static class ServiceTokenCommand
{
    /// <summary>The first arguments this handles.</summary>
    public static readonly IReadOnlyList<string> Commands = ["--issue-token", "--revoke-token", "--list-tokens"];

    /// <summary>Whether <paramref name="args"/> is one of these commands.</summary>
    public static bool Handles(string[] args) => args.Length > 0 && Commands.Contains(args[0]);

    /// <summary>Runs the command in <paramref name="args"/> against the token file at <paramref name="path"/>;
    /// returns the exit code (0 done, 1 refused, 2 usage).</summary>
    public static int Run(string[] args, string path, TextWriter output, TextWriter error)
    {
        var store = new ServiceTokenStore(path);
        try
        {
            switch (args)
            {
                case ["--issue-token", var name, "--role", var role]:
                {
                    var issued = store.Issue(name, role);
                    output.WriteLine(issued.Token);
                    error.WriteLine($"Issued a service token for '{name}' (role '{role}'). It is shown once, above: keep it " +
                        $"as a secret. Only its hash is stored, in {store.Path}. Callers send it as Authorization: Bearer <token>.");
                    return 0;
                }
                case ["--revoke-token", var name]:
                    if (store.Revoke(name))
                    {
                        error.WriteLine($"Revoked the service token for '{name}'.");
                        return 0;
                    }
                    error.WriteLine($"There is no service token for '{name}' in {store.Path}.");
                    return 1;
                case ["--list-tokens"]:
                    foreach (var account in store.List())
                        output.WriteLine($"{account.Name}\t{account.Role}\t{account.Created:yyyy-MM-ddTHH:mm:ssZ}");
                    return 0;
                default:
                    error.WriteLine("usage: --issue-token <name> --role <role> | --revoke-token <name> | --list-tokens");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or JsonException or InvalidDataException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
    }
}

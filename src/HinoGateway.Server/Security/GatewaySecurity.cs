using System.Security.Claims;
using HinoGateway.Core;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace HinoGateway.Server.Security;

/// <summary>The three things a caller may be allowed to do.</summary>
public static class GatewayPolicies
{
    /// <summary>Read values, health and configuration.</summary>
    public const string Viewer = "Viewer";

    /// <summary>Viewer, plus writing tag values and acknowledging alarms.</summary>
    public const string Operator = "Operator";

    /// <summary>Operator, plus changing the configuration.</summary>
    public const string Admin = "Admin";

    /// <summary>Ordered weakest to strongest, so a policy can accept anything above it.</summary>
    public static IReadOnlyList<string> AtLeast(string policy) => policy switch
    {
        Admin => new[] { Admin },
        Operator => new[] { Operator, Admin },
        _ => new[] { Viewer, Operator, Admin },
    };
}

/// <summary>
/// ⚠ <b>Reconstructed on 2026-08-10</b> after the source tree was lost. The behaviour below matches
/// what the README documents and what the call sites require; the original was written by the
/// project owner and may have carried extra members.
/// </summary>
public sealed class GatewaySecurityOptions
{
    public bool Enabled { get; set; }
    public List<GatewayApiKey> ApiKeys { get; set; } = [];

    /// <summary>
    /// Refuse to start a key-protected gateway unless an HTTPS endpoint is explicitly configured.
    /// Redirection alone cannot create a TLS listener, so without this an "enabled" gateway would
    /// happily accept its keys in clear text over the network.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// With no keys configured, a request that arrives from the machine itself is treated as the
    /// local console operator (full rights). Requests from anywhere else are read-only regardless.
    /// Set false to make a keyless gateway read-only for everyone.
    ///
    /// <para>
    /// This only works while "arrives from the machine itself" is provable. Put a reverse proxy on
    /// the same IPC and every request reaches Kestrel from 127.0.0.1 — so unless that proxy is
    /// declared in <see cref="TrustedProxies"/>, the whole LAN would inherit the console's rights.
    /// See <c>GatewayApiKeyHandler.IsLocalRequest</c> for the two checks that close that door.
    /// </para>
    /// </summary>
    public bool AllowLocalConsole { get; set; } = true;

    /// <summary>
    /// Reverse proxies whose <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c> headers may be
    /// believed, as plain IP addresses (<c>127.0.0.1</c>, <c>::1</c>, <c>10.0.0.5</c>).
    ///
    /// <para>
    /// Empty by default, and deliberately so: ASP.NET Core's own default trusts loopback, which is
    /// exactly the address a same-machine proxy arrives from. Trusting it implicitly would mean a
    /// forged header from any local process could rewrite a caller's apparent origin. Naming the
    /// proxy is one line of configuration and makes the trust decision visible in the file that a
    /// commissioning engineer reads.
    /// </para>
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>Same as <see cref="TrustedProxies"/>, in CIDR form (<c>10.20.0.0/24</c>).</summary>
    public List<string> TrustedNetworks { get; set; } = [];

    /// <summary>True when a proxy has been declared, so forwarded headers carry real information.</summary>
    public bool HasTrustedProxy => TrustedProxies.Count > 0 || TrustedNetworks.Count > 0;
}

public static class GatewaySecurityValidation
{
    public static void Validate(GatewaySecurityOptions options, IConfiguration configuration)
    {
        // Parsed first and unconditionally: a typo in a proxy address is a security setting that
        // silently does nothing, which is worse than a gateway that refuses to start.
        foreach (var _ in ParseProxies(options)) { }
        foreach (var _ in ParseNetworks(options)) { }

        if (!options.Enabled)
            return;
        if (options.ApiKeys.Count == 0 || options.ApiKeys.Any(k => string.IsNullOrWhiteSpace(k.Key)))
            throw new InvalidOperationException("Security is enabled but no valid Security:ApiKeys are configured.");

        // A declared proxy makes X-Forwarded-Proto authoritative, so RequireHttps can be honoured
        // without Kestrel itself holding a certificate. Without one there is nothing to go on but
        // Kestrel's own listener, and "enabled" security over plaintext is not security.
        if (options.RequireHttps && !HasHttpsEndpoint(configuration) && !options.HasTrustedProxy)
            throw new InvalidOperationException(
                "Security is enabled with RequireHttps=true, but no HTTPS endpoint is configured and no " +
                "trusted proxy is declared. Configure Kestrel:Endpoints:Https with a certificate, or list " +
                "the TLS-terminating proxy in Security:TrustedProxies, or set Security:RequireHttps=false.");
    }

    /// <summary>
    /// The configuration that lets a keyless gateway hand out Admin to the whole network: no keys,
    /// the local-console shortcut on, a listener that is not loopback, and no declared proxy. Every
    /// one of those is a reasonable choice on its own, which is why the combination needs saying out
    /// loud rather than being left for someone to infer.
    /// </summary>
    public static string? DescribeExposure(GatewaySecurityOptions options, IConfiguration configuration)
    {
        if (options.Enabled || !options.AllowLocalConsole)
            return null;

        var urls = configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Select(e => e["Url"]).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (urls.Count > 0 && urls.All(IsLoopbackUrl))
            return null;

        return options.HasTrustedProxy
            ? "Security is disabled and the gateway is not listening on loopback only. Requests from " +
              "the declared proxy are resolved to their real client address and stay read-only, but " +
              "anyone reaching the port can still read every tag. Enable Security:ApiKeys before commissioning."
            : "Security is DISABLED, the gateway is not listening on loopback only, and no reverse proxy " +
              "is declared in Security:TrustedProxies. If a proxy is in front of this gateway, every " +
              "remote user will be treated as the local console and granted Admin — including PLC writes. " +
              "Declare the proxy, or enable Security:ApiKeys, or set Security:AllowLocalConsole=false.";
    }

    /// <summary>Proxy addresses, parsed and validated. Throws on a malformed entry.</summary>
    public static IEnumerable<System.Net.IPAddress> ParseProxies(GatewaySecurityOptions options)
    {
        foreach (var entry in options.TrustedProxies)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            if (!System.Net.IPAddress.TryParse(entry.Trim(), out var address))
                throw new InvalidOperationException(
                    $"Security:TrustedProxies contains '{entry}', which is not an IP address. Use a plain " +
                    "address such as 127.0.0.1 or ::1; CIDR ranges belong in Security:TrustedNetworks.");
            yield return address;
        }
    }

    /// <summary>Proxy networks in CIDR form, parsed and validated. Throws on a malformed entry.</summary>
    public static IEnumerable<(System.Net.IPAddress Prefix, int Length)> ParseNetworks(GatewaySecurityOptions options)
    {
        foreach (var entry in options.TrustedNetworks)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;
            var text = entry.Trim();
            var slash = text.IndexOf('/');
            if (slash <= 0
                || !System.Net.IPAddress.TryParse(text[..slash], out var prefix)
                || !int.TryParse(text[(slash + 1)..], out var length))
                throw new InvalidOperationException(
                    $"Security:TrustedNetworks contains '{entry}', which is not CIDR notation such as 10.20.0.0/24.");

            var maximum = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
            if (length < 0 || length > maximum)
                throw new InvalidOperationException(
                    $"Security:TrustedNetworks contains '{entry}', whose prefix length must be between 0 and {maximum}.");
            yield return (prefix, length);
        }
    }

    private static bool IsLoopbackUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            return false;
        // "localhost" is a name rather than an address, so Uri resolves it to Loopback for us; a
        // wildcard binding ("+", "*", "0.0.0.0") is by definition not loopback.
        return parsed.IsLoopback;
    }

    private static bool HasHttpsEndpoint(IConfiguration configuration) =>
        configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Any(e => e["Url"]?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true);
}

public sealed class GatewayApiKey
{
    public string Name { get; set; } = "";

    /// <summary>
    /// The key, or a reference to it: <c>${secret:api.hmi}</c> / <c>${env:HINO_API_HMI}</c>.
    ///
    /// <para>
    /// A literal here is a credential living in a configuration file — one that gets copied to a
    /// support ticket, checked into a repository, or read over someone's shoulder. The gateway
    /// already keeps the MQTT password out of the project this way and had no reason to treat the
    /// key that grants Admin over every PLC less carefully. Literals still work, and are warned about
    /// at startup.
    /// </para>
    /// </summary>
    public string Key { get; set; } = "";

    public string Role { get; set; } = GatewayPolicies.Viewer;
}

/// <summary>
/// Failed authentications, counted per source address, with a delay that grows as they repeat.
///
/// <para>
/// There was nothing here at all: an invalid key produced a 401 and no log line, no counter and no
/// cost, so a key could be guessed at as fast as the network allowed and nobody would know it had
/// been attempted. A lockout is the wrong answer for a gateway — the one account is the site's HMI,
/// and locking it out during an incident is worse than the attack — but a second of delay after a
/// handful of failures makes guessing hopeless while a legitimate client that fumbles a key once
/// notices nothing.
/// </para>
/// </summary>
public sealed class AuthFailureTracker
{
    /// <summary>Bounded so that failures from spoofed or rotating addresses cannot grow this map.</summary>
    private const int MaxTrackedSources = 1024;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(2);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> _sources = new();
    private long _total;

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset WindowStart = DateTimeOffset.UtcNow;
    }

    /// <summary>Failed authentications since startup, for the health and diagnostics surface.</summary>
    public long TotalFailures => Interlocked.Read(ref _total);

    /// <summary>How many sources are currently being tracked.</summary>
    public int TrackedSources => _sources.Count;

    /// <summary>Record a failure and return how long this caller should be held before answering.</summary>
    public TimeSpan Record(string source)
    {
        Interlocked.Increment(ref _total);

        if (_sources.Count >= MaxTrackedSources && !_sources.ContainsKey(source))
            return MaxDelay;   // out of room to track: assume the worst rather than stop counting

        var now = DateTimeOffset.UtcNow;
        var entry = _sources.GetOrAdd(source, _ => new Entry());
        lock (entry)
        {
            if (now - entry.WindowStart > Window)
            {
                entry.WindowStart = now;
                entry.Failures = 0;
            }
            entry.Failures++;

            // The first few are free: a client with a stale key after a rotation should fail fast and
            // legibly, not appear to hang.
            if (entry.Failures <= 3)
                return TimeSpan.Zero;
            var delay = TimeSpan.FromMilliseconds(Math.Min(MaxDelay.TotalMilliseconds, 100 * Math.Pow(2, entry.Failures - 4)));
            return delay;
        }
    }

    /// <summary>A successful authentication clears the source's history.</summary>
    public void Succeeded(string source) => _sources.TryRemove(source, out _);
}

/// <summary>
/// Authenticates a request by its <c>X-Api-Key</c> header.
///
/// <para>
/// Note what happens when security is switched off: the request is still given an identity, and that
/// identity is Viewer unless it came from the machine itself. Treating an unauthenticated request as
/// a full operator — as this did before — meant that anyone who could reach the port could command a
/// PLC.
/// </para>
/// </summary>
public sealed class GatewayApiKeyHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "GatewayApiKey";
    public const string HeaderName = "X-Api-Key";

    private readonly GatewaySecurityOptions _security;
    private readonly ISecretStore _secrets;
    private readonly AuthFailureTracker _failures;

    public GatewayApiKeyHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        GatewaySecurityOptions security,
        ISecretStore secrets,
        AuthFailureTracker failures) : base(options, logger, encoder)
    {
        _security = security;
        _secrets = secrets;
        _failures = failures;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_security.Enabled)
        {
            // No keys configured: trust the console of the machine itself, nothing else.
            return _security.AllowLocalConsole && IsLocalRequest()
                ? Success("local-console", GatewayPolicies.Admin)
                : Success("anonymous", GatewayPolicies.Viewer);
        }

        var supplied = Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(supplied) && Request.Path.StartsWithSegments("/hubs"))
        {
            var bearer = Request.Headers.Authorization.ToString();
            if (bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                supplied = bearer[7..];
            else
                supplied = Request.Query["access_token"].ToString();
        }
        if (string.IsNullOrEmpty(supplied))
            return AuthenticateResult.NoResult();

        var source = Context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var match = _security.ApiKeys.FirstOrDefault(candidate => SecureEquals(Resolve(candidate), supplied));
        if (match is not null)
        {
            _failures.Succeeded(source);
            return Success(string.IsNullOrWhiteSpace(match.Name) ? "api-key" : match.Name, match.Role);
        }

        // Logged, counted and slowed down. Note what is not logged: the key that was offered. A
        // rejected credential is still a credential, and a log an operator is told to send to support
        // should not be the thing that leaks the one they mistyped it from.
        var delay = _failures.Record(source);
        Logger.LogWarning(
            "Rejected an API key from {Source} on {Path} ({Failures} failure(s) tracked overall)",
            source, Request.Path, _failures.TotalFailures);
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, Context.RequestAborted).ConfigureAwait(false);

        return AuthenticateResult.Fail("Invalid API key.");
    }

    /// <summary>
    /// The configured key, or the secret it points at. An unresolvable reference authenticates
    /// nobody: returning the reference text itself would turn <c>${secret:api.hmi}</c> into a
    /// password that works for anyone who has read the configuration file.
    /// </summary>
    private string Resolve(GatewayApiKey key)
    {
        if (!SecretRef.TryParse(key.Key, out var reference))
            return key.Key;

        var value = reference.Source == SecretSource.Environment
            ? Environment.GetEnvironmentVariable(reference.Name)
            : _secrets.TryGet(reference.Name, out var stored) ? stored : null;

        if (!string.IsNullOrEmpty(value))
            return value;

        Logger.LogError("API key '{Name}' references {Reference}, which does not resolve; it cannot authenticate anyone",
            key.Name, key.Key);
        return "";
    }

    /// <summary>
    /// Is this request provably the console of the machine the gateway runs on?
    ///
    /// <para>
    /// The bar is "provably", not "plausibly", because the answer decides whether an unauthenticated
    /// caller may command a PLC. Two things had to go:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// A missing peer address used to count as local. It proves nothing — it means the transport did
    /// not supply one — and it made every in-process caller an administrator.
    /// </item>
    /// <item>
    /// <c>remote.Equals(local)</c> used to count as local. On a gateway bound to a LAN address that
    /// is only ever true for a same-machine caller, so it added nothing that the loopback test does
    /// not already cover, while reading as though it did.
    /// </item>
    /// </list>
    /// <para>
    /// What remains is the case this had to be built for: a reverse proxy on the same IPC. Every
    /// request then reaches Kestrel from 127.0.0.1, and without the check below the entire LAN would
    /// be treated as the console. A proxy declared in <c>Security:TrustedProxies</c> is resolved back
    /// to the real client by the forwarded-headers middleware before this runs. A proxy that was not
    /// declared leaves its header in place — and a header that survived that middleware is proof the
    /// request was relayed by someone we were not told to trust, so it cannot be the console either.
    /// </para>
    /// </summary>
    private bool IsLocalRequest()
    {
        if (Request.Headers.ContainsKey("X-Forwarded-For") || Request.Headers.ContainsKey("Forwarded"))
            return false;

        var remote = Context.Connection.RemoteIpAddress;
        return remote is not null && System.Net.IPAddress.IsLoopback(remote);
    }

    private AuthenticateResult Success(string name, string role)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role)], SchemeName);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    /// <summary>Fixed-time comparison: a key check that returns early leaks the key one byte at a time.</summary>
    private static bool SecureEquals(string a, string b)
    {
        var left = Encoding.UTF8.GetBytes(a ?? "");
        var right = Encoding.UTF8.GetBytes(b ?? "");
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            left.Length == right.Length ? left : new byte[right.Length], right);
    }
}

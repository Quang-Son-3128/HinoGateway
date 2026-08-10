using System.Security.Claims;
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
    /// </summary>
    public bool AllowLocalConsole { get; set; } = true;
}

public static class GatewaySecurityValidation
{
    public static void Validate(GatewaySecurityOptions options, IConfiguration configuration)
    {
        if (!options.Enabled)
            return;
        if (options.ApiKeys.Count == 0 || options.ApiKeys.Any(k => string.IsNullOrWhiteSpace(k.Key)))
            throw new InvalidOperationException("Security is enabled but no valid Security:ApiKeys are configured.");
        if (options.RequireHttps && !HasHttpsEndpoint(configuration))
            throw new InvalidOperationException(
                "Security is enabled with RequireHttps=true, but no HTTPS endpoint is configured. " +
                "Configure Kestrel:Endpoints:Https with a certificate, or set Security:RequireHttps=false " +
                "only when a trusted reverse proxy terminates TLS.");
    }

    private static bool HasHttpsEndpoint(IConfiguration configuration) =>
        configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Any(e => e["Url"]?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true);
}

public sealed class GatewayApiKey
{
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public string Role { get; set; } = GatewayPolicies.Viewer;
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

    public GatewayApiKeyHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        GatewaySecurityOptions security) : base(options, logger, encoder)
    {
        _security = security;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_security.Enabled)
        {
            // No keys configured: trust the console of the machine itself, nothing else.
            return Task.FromResult(_security.AllowLocalConsole && IsLocalRequest()
                ? Success("local-console", GatewayPolicies.Admin)
                : Success("anonymous", GatewayPolicies.Viewer));
        }

        var supplied = Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(supplied))
            return Task.FromResult(AuthenticateResult.NoResult());

        var match = _security.ApiKeys.FirstOrDefault(candidate => SecureEquals(candidate.Key, supplied));
        return Task.FromResult(match is null
            ? AuthenticateResult.Fail("Invalid API key.")
            : Success(string.IsNullOrWhiteSpace(match.Name) ? "api-key" : match.Name, match.Role));
    }

    private bool IsLocalRequest()
    {
        var remote = Context.Connection.RemoteIpAddress;
        if (remote is null)
            return true;                     // in-process call
        var local = Context.Connection.LocalIpAddress;
        return System.Net.IPAddress.IsLoopback(remote) || remote.Equals(local);
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

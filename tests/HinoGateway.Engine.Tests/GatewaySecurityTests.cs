using HinoGateway.Server.Security;
using Microsoft.Extensions.Configuration;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// The gateway's keyless mode hands Admin — tag writes, PLC commands, configuration changes — to
/// whoever it believes is the console of its own machine. These tests are about what it is allowed
/// to believe, because the combination that got it wrong is not an exotic one: a reverse proxy on
/// the same industrial PC, which is what the deployment guide recommends for terminating TLS.
/// </summary>
public class GatewaySecurityTests
{
    private static IConfiguration Kestrel(params string[] urls)
    {
        var settings = new Dictionary<string, string?>();
        for (var i = 0; i < urls.Length; i++)
            settings[$"Kestrel:Endpoints:E{i}:Url"] = urls[i];
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    /// <summary>
    /// The host names that mean "this machine". Table-driven because the interesting cases are the
    /// spellings, not the logic: an address is checked as an address, so the whole 127.0.0.0/8 range
    /// counts and not just the one way of writing it, while a name that merely resolves to loopback
    /// today — which is the entire DNS-rebinding trick — does not.
    /// </summary>
    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LocalHost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.0.0.53", true)]
    [InlineData("::1", true)]
    [InlineData("evil.example", false)]
    [InlineData("gateway.plant.local", false)]
    [InlineData("10.20.0.55", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_machines_own_names_count_as_the_console(string? host, bool expected) =>
        Assert.Equal(expected, GatewayApiKeyHandler.IsLoopbackHost(host));

    [Theory]
    [InlineData("http://localhost:2000")]
    [InlineData("http://127.0.0.1:2000")]
    [InlineData("https://[::1]:2443")]
    public void A_gateway_listening_only_on_loopback_is_not_exposed(string url)
    {
        var options = new GatewaySecurityOptions { Enabled = false, AllowLocalConsole = true };
        Assert.Null(GatewaySecurityValidation.DescribeExposure(options, Kestrel(url)));
    }

    [Theory]
    [InlineData("http://0.0.0.0:2000")]
    [InlineData("http://192.168.1.50:2000")]
    public void A_keyless_gateway_on_the_network_with_no_declared_proxy_is_called_out(string url)
    {
        var options = new GatewaySecurityOptions { Enabled = false, AllowLocalConsole = true };

        var warning = GatewaySecurityValidation.DescribeExposure(options, Kestrel(url));

        // The warning has to name the consequence, not the setting. "AllowLocalConsole is true" is
        // something the operator already knows; "every remote user gets Admin" is the part that
        // makes them act on it.
        Assert.NotNull(warning);
        Assert.Contains("Admin", warning);
        Assert.Contains("TrustedProxies", warning);
    }

    [Fact]
    public void A_mixed_binding_counts_as_exposed()
    {
        // One loopback endpoint does not make the wildcard one safe, and reading the list as "some
        // of these are local" is exactly how this gets missed on a two-endpoint HTTP/HTTPS setup.
        var options = new GatewaySecurityOptions { Enabled = false, AllowLocalConsole = true };
        Assert.NotNull(GatewaySecurityValidation.DescribeExposure(
            options, Kestrel("http://localhost:2000", "https://0.0.0.0:2443")));
    }

    [Fact]
    public void Declaring_the_proxy_downgrades_the_warning_but_does_not_remove_it()
    {
        var options = new GatewaySecurityOptions
        {
            Enabled = false,
            AllowLocalConsole = true,
            TrustedProxies = ["127.0.0.1"],
        };

        var warning = GatewaySecurityValidation.DescribeExposure(options, Kestrel("http://0.0.0.0:2000"));

        // Remote users are no longer mistaken for the console, but a keyless gateway on the network
        // still lets anyone read every tag, and that is worth saying before commissioning.
        Assert.NotNull(warning);
        Assert.DoesNotContain("Admin", warning);
        Assert.Contains("read every tag", warning);
    }

    [Theory]
    [InlineData(true, true)]    // keys configured: the local-console shortcut is not in play
    [InlineData(false, false)]  // shortcut switched off explicitly
    public void There_is_nothing_to_warn_about_once_the_shortcut_cannot_grant_anything(
        bool enabled, bool allowLocalConsole)
    {
        var options = new GatewaySecurityOptions
        {
            Enabled = enabled,
            AllowLocalConsole = allowLocalConsole,
            ApiKeys = enabled ? [new GatewayApiKey { Name = "hmi", Key = "k", Role = "Operator" }] : [],
        };

        Assert.Null(GatewaySecurityValidation.DescribeExposure(options, Kestrel("http://0.0.0.0:2000")));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.20.0.0/24")]   // a range belongs in TrustedNetworks, and silently ignoring it
    [InlineData("")]               // would leave the operator believing a proxy was trusted
    public void A_proxy_address_that_is_not_an_address_fails_the_gateway_at_startup(string entry)
    {
        var options = new GatewaySecurityOptions { TrustedProxies = [entry] };

        if (string.IsNullOrWhiteSpace(entry))
        {
            // Blank entries are the ordinary residue of editing a JSON list and mean nothing.
            GatewaySecurityValidation.Validate(options, Kestrel("http://localhost:2000"));
            return;
        }

        var error = Assert.Throws<InvalidOperationException>(
            () => GatewaySecurityValidation.Validate(options, Kestrel("http://localhost:2000")));
        Assert.Contains(entry, error.Message);
    }

    [Theory]
    [InlineData("10.20.0.0")]      // no prefix length
    [InlineData("10.20.0.0/33")]   // out of range for IPv4
    [InlineData("10.20.0.0/-1")]
    public void A_malformed_proxy_network_fails_the_gateway_at_startup(string entry)
    {
        var options = new GatewaySecurityOptions { TrustedNetworks = [entry] };

        var error = Assert.Throws<InvalidOperationException>(
            () => GatewaySecurityValidation.Validate(options, Kestrel("http://localhost:2000")));
        Assert.Contains(entry, error.Message);
    }

    [Fact]
    public void A_well_formed_proxy_declaration_is_accepted()
    {
        var options = new GatewaySecurityOptions
        {
            TrustedProxies = ["127.0.0.1", "::1"],
            TrustedNetworks = ["10.20.0.0/24", "fd00::/8"],
        };

        GatewaySecurityValidation.Validate(options, Kestrel("http://localhost:2000"));

        Assert.Equal(2, GatewaySecurityValidation.ParseProxies(options).Count());
        Assert.Equal(2, GatewaySecurityValidation.ParseNetworks(options).Count());
        Assert.True(options.HasTrustedProxy);
    }

    /// <summary>
    /// RequireHttps used to demand a Kestrel HTTPS listener, which a gateway behind a TLS-terminating
    /// proxy does not have — so the only way to start was to switch the guard off entirely. With the
    /// proxy declared, X-Forwarded-Proto is trustworthy and the guard can stay on.
    /// </summary>
    [Fact]
    public void RequireHttps_is_satisfied_by_a_declared_proxy_instead_of_forcing_it_off()
    {
        var withProxy = new GatewaySecurityOptions
        {
            Enabled = true,
            RequireHttps = true,
            TrustedProxies = ["127.0.0.1"],
            ApiKeys = [new GatewayApiKey { Name = "hmi", Key = "k", Role = "Operator" }],
        };
        GatewaySecurityValidation.Validate(withProxy, Kestrel("http://0.0.0.0:2000"));

        var withoutProxy = new GatewaySecurityOptions
        {
            Enabled = true,
            RequireHttps = true,
            ApiKeys = [new GatewayApiKey { Name = "hmi", Key = "k", Role = "Operator" }],
        };
        var error = Assert.Throws<InvalidOperationException>(
            () => GatewaySecurityValidation.Validate(withoutProxy, Kestrel("http://0.0.0.0:2000")));
        Assert.Contains("TrustedProxies", error.Message);
    }
}

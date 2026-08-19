using System.Net;

namespace HinoGateway.Api.Tests;

/// <summary>
/// The keyless mode, and the reverse-proxy hole that used to be underneath it.
///
/// <para>
/// With no keys configured the gateway treats the console of its own machine as an administrator —
/// which is the right convenience for an engineer standing at the industrial PC, and was a hole the
/// moment anything sat in front of Kestrel. A reverse proxy on the same box, the topology the
/// deployment guide recommends for terminating TLS, makes every request arrive from 127.0.0.1: the
/// whole LAN inherited Admin, and the audit trail recorded all of them as <c>local-console</c>.
/// </para>
///
/// <para>
/// These are the tests that would have caught it, and the ones that stop it coming back.
/// </para>
/// </summary>
public class LocalConsoleTests
{
    private const string WriteRoute = "/api/tags/write";
    private static readonly object AWrite = new { tagId = "no/such/tag", value = 1 };

    /// <summary>
    /// Reaching a route at all, as opposed to being turned away by authorisation. 404 and 400 both
    /// mean the caller was allowed in — the request simply named nothing real, which is deliberate:
    /// these tests must not depend on a configured device.
    /// </summary>
    private static bool WasAllowed(HttpResponseMessage response) =>
        response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

    [Fact]
    public async Task The_machines_own_console_is_still_an_administrator()
    {
        using var gateway = new GatewayFactory();
        using var client = gateway.CreateClient(null);

        var response = await client.PostJsonAsync(WriteRoute, AWrite);

        Assert.True(WasAllowed(response),
            "an engineer at the industrial PC must still be able to command it without a key");
    }

    [Fact]
    public async Task A_remote_caller_is_read_only()
    {
        using var gateway = new GatewayFactory(remoteAddress: "10.20.0.55");
        using var client = gateway.CreateClient(null);

        Assert.True(WasAllowed(await client.GetAsync("/api/tags")));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostJsonAsync(WriteRoute, AWrite)).StatusCode);
    }

    [Theory]
    [InlineData("X-Forwarded-For", "10.20.0.55")]
    [InlineData("X-Forwarded-For", "10.20.0.55, 127.0.0.1")]
    [InlineData("Forwarded", "for=10.20.0.55")]
    public async Task A_request_relayed_by_an_undeclared_proxy_never_gets_admin(string header, string value)
    {
        // The exact shape of the original hole: the connection is from loopback because the proxy is
        // on the same machine, and the real caller is somewhere on the LAN.
        using var gateway = new GatewayFactory();
        using var client = gateway.CreateClient(null);
        client.DefaultRequestHeaders.Add(header, value);

        var response = await client.PostJsonAsync(WriteRoute, AWrite);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_declared_proxy_resolves_the_caller_to_their_real_address()
    {
        using var gateway = new GatewayFactory(new Dictionary<string, string?>
        {
            ["Security:TrustedProxies:0"] = "127.0.0.1",
        });
        using var client = gateway.CreateClient(null);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "10.20.0.55");

        // Resolved to a LAN address, so not the console: read-only, and correctly so.
        Assert.True(WasAllowed(await client.GetAsync("/api/tags")));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostJsonAsync(WriteRoute, AWrite)).StatusCode);
    }

    [Fact]
    public async Task Switching_off_the_console_shortcut_makes_even_the_machine_read_only()
    {
        using var gateway = new GatewayFactory(new Dictionary<string, string?>
        {
            ["Security:AllowLocalConsole"] = "false",
        });
        using var client = gateway.CreateClient(null);

        Assert.True(WasAllowed(await client.GetAsync("/api/tags")));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostJsonAsync(WriteRoute, AWrite)).StatusCode);
    }

    [Fact]
    public async Task A_keyless_gateway_still_refuses_configuration_changes_from_the_network()
    {
        using var gateway = new GatewayFactory(remoteAddress: "10.20.0.55");
        using var client = gateway.CreateClient(null);

        foreach (var route in new[] { "/api/config/devices", "/api/config/export" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(route)).StatusCode);
    }
}

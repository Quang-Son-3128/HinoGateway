using System.Net;

namespace HinoGateway.Api.Tests;

/// <summary>
/// Every endpoint, against every role, over real HTTP.
///
/// <para>
/// The point is not that these particular routes behave correctly today — a reader can see that from
/// the attributes. It is that the attributes cannot be removed, weakened or forgotten on a new
/// endpoint without something failing. On a gateway that writes to PLCs, "an endpoint lost its
/// <c>[Authorize]</c>" is not a defect that should be caught by whoever happens to review the diff.
/// </para>
///
/// <para>
/// Written as data rather than as one test per route so that adding a route means adding a line.
/// A test that is tedious to extend is a test that stops being extended.
/// </para>
/// </summary>
public class AuthorizationMatrixTests
{
    private const string ViewerKey = "viewer-key-000000000000000000000";
    private const string OperatorKey = "operator-key-00000000000000000000";
    private const string AdminKey = "admin-key-0000000000000000000000";

    /// <summary>Keys configured, and every request arriving from the LAN rather than the console.</summary>
    private static GatewayFactory Secured() => new(new Dictionary<string, string?>
    {
        ["Security:Enabled"] = "true",
        ["Security:RequireHttps"] = "false",
        ["Security:ApiKeys:0:Name"] = "viewer",
        ["Security:ApiKeys:0:Key"] = ViewerKey,
        ["Security:ApiKeys:0:Role"] = "Viewer",
        ["Security:ApiKeys:1:Name"] = "operator",
        ["Security:ApiKeys:1:Key"] = OperatorKey,
        ["Security:ApiKeys:1:Role"] = "Operator",
        ["Security:ApiKeys:2:Name"] = "admin",
        ["Security:ApiKeys:2:Key"] = AdminKey,
        ["Security:ApiKeys:2:Role"] = "Admin",
    }, remoteAddress: "10.20.0.55");

    /// <summary>Route, HTTP method, and the weakest role that may reach it.</summary>
    public static TheoryData<string, string, string> Endpoints => new()
    {
        // Viewer — reading values, health and diagnostics.
        { "GET", "/api/tags", "Viewer" },
        { "GET", "/api/tags/values", "Viewer" },
        { "GET", "/api/stats", "Viewer" },
        { "GET", "/api/health", "Viewer" },
        { "GET", "/api/health/components", "Viewer" },
        { "GET", "/api/diagnostics/blocks", "Viewer" },
        { "GET", "/api/diagnostics/transports", "Viewer" },
        { "GET", "/api/alarms", "Viewer" },
        { "GET", "/api/alarms/active", "Viewer" },
        { "GET", "/api/alarms/states", "Viewer" },
        { "GET", "/api/alarms/summary", "Viewer" },
        { "GET", "/api/alarms/events", "Viewer" },
        { "GET", "/api/energy/state", "Viewer" },
        { "GET", "/api/energy/periods", "Viewer" },
        { "GET", "/api/history/status", "Viewer" },
        { "GET", "/api/iec104/status", "Viewer" },
        { "GET", "/api/mqtt/status", "Viewer" },
        { "GET", "/api/expressions/functions", "Viewer" },

        // Operator — acting on the plant.
        { "POST", "/api/tags/write", "Operator" },
        { "POST", "/api/alarms/ack", "Operator" },

        // Admin — changing what the gateway is.
        { "GET", "/api/config/devices", "Admin" },
        { "GET", "/api/config/tags", "Admin" },
        { "GET", "/api/config/groups", "Admin" },
        { "GET", "/api/config/drivers", "Admin" },
        { "GET", "/api/config/export", "Admin" },
        { "POST", "/api/config/reload", "Admin" },
        { "POST", "/api/config/import", "Admin" },
        { "POST", "/api/alarms/reload", "Admin" },
        { "GET", "/api/mqtt", "Admin" },
        { "POST", "/api/mqtt/apply", "Admin" },
        { "POST", "/api/iec104/apply", "Admin" },
        { "POST", "/api/iec104/points/csv", "Admin" },
        // A command is a standing authorisation to move a setpoint, so even reading the list is Admin.
        { "GET", "/api/commands", "Admin" },
        { "POST", "/api/commands/check", "Admin" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_role_below_the_requirement_is_refused(string method, string route, string required)
    {
        using var gateway = Secured();

        foreach (var (role, key) in new[] { ("Viewer", ViewerKey), ("Operator", OperatorKey), ("Admin", AdminKey) })
        {
            using var client = gateway.CreateClient(key);
            var response = await Send(client, method, route);

            if (Rank(role) >= Rank(required))
            {
                // Anything but a refusal. The endpoint may well answer 400 or 404 — these requests
                // carry no body and name nothing that exists — and that is the point: it got past
                // authorisation and started doing its job.
                Assert.False(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                    $"{role} was refused {method} {route}, which needs only {required}");
            }
            else
            {
                Assert.True(response.StatusCode is HttpStatusCode.Forbidden,
                    $"{role} got {(int)response.StatusCode} for {method} {route}, which needs {required}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task No_key_reaches_nothing_at_all(string method, string route, string required)
    {
        _ = required;
        using var gateway = Secured();
        using var client = gateway.CreateClient(null);

        var response = await Send(client, method, route);

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"an unauthenticated caller got {(int)response.StatusCode} for {method} {route}");
    }

    [Fact]
    public async Task A_wrong_key_is_refused_and_does_not_say_why()
    {
        using var gateway = Secured();
        using var client = gateway.CreateClient("not-the-key");

        var response = await client.GetAsync("/api/tags");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // A 401 that distinguished "no such key" from "that key cannot do this" would be an oracle
        // for guessing which keys exist.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("viewer", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Health_endpoints_stay_open_because_a_monitor_has_no_key()
    {
        using var gateway = Secured();
        using var client = gateway.CreateClient(null);

        foreach (var route in new[] { "/health/live", "/health/ready" })
        {
            var response = await client.GetAsync(route);
            Assert.False(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                $"{route} requires authentication, so nothing can watch this gateway");
        }
    }

    [Fact]
    public async Task Checking_a_formula_is_not_an_audit_event()
    {
        // The audit writer holds each caller until its batch has reached the disk — right for a PLC
        // write, wrong for the formula editor, which POSTs on every keystroke because a formula does
        // not fit in a query string. Both checking endpoints sit at Viewer level, so on a keyless
        // gateway this was a way for anyone on the LAN to drive the disk and fill the audit files
        // with events that are not events.
        using var gateway = Secured();
        using var client = gateway.CreateClient(ViewerKey);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(HttpStatusCode.OK,
                (await client.PostJsonAsync("/api/expressions/validate", new { expression = "1+1" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK,
                (await client.PostJsonAsync("/api/expressions/evaluate", new { expression = "1+1" })).StatusCode);
        }

        Assert.Empty(AuditLines(gateway));
    }

    [Fact]
    public async Task A_configuration_change_is_still_audited()
    {
        // The other side of it: the guard above must not be a hole for the requests the trail exists
        // to record.
        using var gateway = Secured();
        using var client = gateway.CreateClient(AdminKey);

        await client.PostJsonAsync("/api/config/reload", new { });

        Assert.Contains(AuditLines(gateway), line =>
            line.Contains("\"actor\":\"admin\"") && line.Contains("/api/config/reload"));
    }

    /// <summary>Everything the audit writer has put on disk for this gateway, as raw JSONL lines.</summary>
    private static IReadOnlyList<string> AuditLines(GatewayFactory gateway)
    {
        var directory = Path.Combine(gateway.DataRoot, "audit");
        if (!Directory.Exists(directory))
            return Array.Empty<string>();
        // FileShare.ReadWrite because the audit writer keeps its file open for the life of the
        // gateway; File.ReadAllLines asks for FileShare.Read and is refused.
        return Directory.EnumerateFiles(directory)
            .SelectMany(ReadSharedLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    private static IEnumerable<string> ReadSharedLines(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    private static int Rank(string role) => role switch
    {
        "Admin" => 3,
        "Operator" => 2,
        _ => 1,
    };

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string route) =>
        method == "GET"
            ? client.GetAsync(route)
            : client.PostJsonAsync(route, new { });
}

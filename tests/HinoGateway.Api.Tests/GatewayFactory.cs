using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HinoGateway.Api.Tests;

/// <summary>
/// Boots the real gateway in-process, over a temporary data root.
///
/// <para>
/// This project exists because there was nothing between the unit tests and a running gateway on an
/// industrial PC. Thirty-odd test files covered the engine, the drivers, the stores and the protocol
/// codecs, and not one of them went through the HTTP pipeline — so authentication, authorisation,
/// forwarded headers, model binding and the middleware order were held together entirely by review.
/// An endpoint quietly losing its <c>[Authorize]</c>, or a policy dropping from Admin to Viewer, was
/// a change no test in the repository could notice.
/// </para>
///
/// <para>
/// Nothing here replaces a service. What is under test is the composition the gateway actually ships
/// with — a test that swaps out the security handler proves only that the test's handler works.
/// </para>
/// </summary>
public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;
    private readonly string _dataRoot;
    private readonly string _remoteAddress;

    /// <param name="remoteAddress">
    /// The address every request appears to come from. It has to be stated rather than inherited:
    /// <c>TestServer</c> leaves <c>RemoteIpAddress</c> null, which is neither loopback nor a LAN
    /// address, so without this these tests would exercise a case that cannot occur over a socket.
    /// </param>
    public GatewayFactory(Dictionary<string, string?>? settings = null, string remoteAddress = "127.0.0.1")
    {
        _remoteAddress = remoteAddress;
        _dataRoot = Path.Combine(Path.GetTempPath(), "hino-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        _settings = new Dictionary<string, string?>
        {
            ["Data:Root"] = _dataRoot,
            ["Data:SeedDemo"] = "false",
            // Otherwise the historian writes a file a minute into the temp directory for the whole run.
            ["History:Enabled"] = "false",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            _settings[key] = value;
    }

    public string DataRoot => _dataRoot;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);

        // UseSetting rather than ConfigureAppConfiguration. The gateway reads its security options
        // off builder.Configuration while the host is still being constructed, and a configuration
        // source added by the factory has not been applied at that point — so the app started with
        // security switched off and every "unauthenticated caller is refused" assertion passed
        // against a gateway that had no keys at all. Host settings are in place before Program runs.
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);

        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(_remoteAddress)));
    }

    public HttpClient CreateClient(string? apiKey)
    {
        var client = CreateClient();
        if (apiKey is not null)
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    /// <summary>Stamps the connection's remote address before anything else in the pipeline runs.</summary>
    private sealed class RemoteAddressFilter(string address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(address);
                await following(context);
            });
            next(app);
        };
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Shorthand the tests read better with.</summary>
public static class HttpClientExtensions
{
    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body) =>
        client.PostAsync(url, new StringContent(
            System.Text.Json.JsonSerializer.Serialize(body),
            System.Text.Encoding.UTF8,
            new MediaTypeHeaderValue("application/json")));
}

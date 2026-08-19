using System.Net;

namespace HinoGateway.Api.Tests;

/// <summary>
/// The limits on <c>/api/history</c>, which needs only the Viewer role — the role every request from
/// the network is given on a gateway with security switched off. Without a tag list it used to
/// collect every value of every tag over the requested window into a List before writing a byte.
/// </summary>
public class HistoryEndpointTests
{
    [Fact]
    public async Task Reading_every_tag_at_once_is_refused()
    {
        using var gateway = new GatewayFactory();
        using var client = gateway.CreateClient(null);

        var response = await client.GetAsync("/api/history");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("tags", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_named_tag_list_is_answered()
    {
        using var gateway = new GatewayFactory();
        using var client = gateway.CreateClient(null);

        var response = await client.GetAsync("/api/history?tags=Plant/Power,Plant/Energy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unreasonable_tag_list_is_refused_rather_than_attempted()
    {
        using var gateway = new GatewayFactory();
        using var client = gateway.CreateClient(null);
        var tags = string.Join(',', Enumerable.Range(0, 500).Select(i => $"Plant/Tag{i}"));

        var response = await client.GetAsync($"/api/history?tags={tags}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at most", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_backwards_time_range_is_refused()
    {
        using var gateway = new GatewayFactory();
        using var client = gateway.CreateClient(null);

        var response = await client.GetAsync(
            "/api/history?tags=Plant/Power&from=2026-08-18T10:00:00Z&to=2026-08-18T09:00:00Z");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

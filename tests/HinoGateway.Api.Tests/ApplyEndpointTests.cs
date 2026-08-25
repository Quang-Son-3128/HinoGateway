using System.Net;

namespace HinoGateway.Api.Tests;

/// <summary>
/// Applying the stored configuration validates the whole project first, so any rule that a
/// never-configured gateway cannot satisfy takes apply down with it — for every edit, not just the
/// one that touched that area.
/// </summary>
public class ApplyEndpointTests
{
    /// <summary>
    /// A gateway that has never been near an MQTT broker still has to be able to apply.
    ///
    /// <para>
    /// It could not: MQTT's client id defaults to empty on purpose (the publisher derives
    /// <c>hino-{machine}</c> from it, so two gateways do not fight over one broker session), while
    /// validation demanded a non-empty one. Every reload on a fresh gateway failed with "Client ID is
    /// required" — an error naming a feature the operator had not switched on, raised by a button
    /// about connections and tags.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_gateway_with_no_mqtt_configuration_can_still_apply()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        var applied = await client.PostAsync("/api/config/reload", content: null);

        Assert.True(applied.StatusCode != HttpStatusCode.BadRequest,
            await applied.Content.ReadAsStringAsync());
    }
}

using HinoGateway.Northbound.Mqtt;
using MQTTnet;
using MQTTnet.Packets;

namespace HinoGateway.Engine.Tests;

public sealed class MqttPublisherResultTests
{
    [Fact]
    public void Rejected_connect_result_is_not_treated_as_a_live_session() =>
        Assert.Throws<InvalidOperationException>(() =>
            MqttPublisher.EnsureConnectSucceeded(MqttClientConnectResultCode.NotAuthorized, "bad credentials"));

    [Fact]
    public void Rejected_puback_is_not_allowed_to_remove_the_spool_item()
    {
        var rejected = new MqttClientPublishResult(null, MqttClientPublishReasonCode.NotAuthorized,
            "topic ACL", Array.Empty<MqttUserProperty>());
        Assert.Throws<InvalidOperationException>(() => MqttPublisher.EnsurePublishSucceeded(rejected));
    }

    [Theory]
    [InlineData(MqttClientPublishReasonCode.Success)]
    [InlineData(MqttClientPublishReasonCode.NoMatchingSubscribers)]
    public void Successful_puback_is_accepted(MqttClientPublishReasonCode code)
    {
        var accepted = new MqttClientPublishResult(null, code, null!, Array.Empty<MqttUserProperty>());
        MqttPublisher.EnsurePublishSucceeded(accepted);
    }
}

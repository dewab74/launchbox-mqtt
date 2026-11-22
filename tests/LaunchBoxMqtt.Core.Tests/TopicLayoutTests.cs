using LaunchBoxMqtt.Core.Topics;

namespace LaunchBoxMqtt.Core.Tests;

public class TopicLayoutTests
{
    [Fact]
    public void Topics_follow_the_documented_layout()
    {
        var topics = new TopicLayout("launchbox", "homeassistant", "arcade_pc");

        Assert.Equal("launchbox/arcade_pc/state", topics.StateTopic);
        Assert.Equal("launchbox/arcade_pc/availability", topics.AvailabilityTopic);
        Assert.Equal("launchbox_arcade_pc", topics.DeviceId);
        Assert.Equal("homeassistant/sensor/launchbox_arcade_pc/now_playing/config", topics.DiscoveryTopic("sensor", "now_playing"));
    }

    [Fact]
    public void Prefixes_can_have_several_levels()
    {
        var topics = new TopicLayout("home/games", "ha/discovery", "den");
        Assert.Equal("home/games/den/state", topics.StateTopic);
        Assert.Equal("ha/discovery/binary_sensor/launchbox_den/playing/config", topics.DiscoveryTopic("binary_sensor", "playing"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("launch+box")]
    [InlineData("launchbox/#")]
    [InlineData("/launchbox")]
    [InlineData("launchbox/")]
    [InlineData("a//b")]
    [InlineData("launch box")]
    public void Bad_prefixes_are_rejected(string prefix)
    {
        Assert.NotNull(TopicLayout.CheckPrefix(prefix));
        Assert.Throws<ArgumentException>(() => new TopicLayout(prefix, "homeassistant", "pc"));
        Assert.Throws<ArgumentException>(() => new TopicLayout("launchbox", prefix, "pc"));
    }

    [Fact]
    public void Unnormalised_node_id_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new TopicLayout("launchbox", "homeassistant", "My PC"));
}

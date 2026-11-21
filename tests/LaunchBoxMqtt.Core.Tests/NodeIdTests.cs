using LaunchBoxMqtt.Core.Topics;

namespace LaunchBoxMqtt.Core.Tests;

public class NodeIdTests
{
    [Theory]
    [InlineData("COCKTAIL", "cocktail")]
    [InlineData("Arcade PC #2", "arcade_pc_2")]
    [InlineData("  den-pc  ", "den-pc")]
    [InlineData("kid's/room+pc", "kid_s_room_pc")]
    [InlineData("Ünïcode", "n_code")]
    [InlineData("___", NodeId.Fallback)]
    [InlineData("", NodeId.Fallback)]
    [InlineData(null, NodeId.Fallback)]
    public void FromName_normalises_to_discovery_safe_id(string? input, string expected) =>
        Assert.Equal(expected, NodeId.FromName(input));

    [Fact]
    public void FromName_is_idempotent()
    {
        var once = NodeId.FromName("My Arcade (Basement)");
        Assert.Equal(once, NodeId.FromName(once));
    }
}

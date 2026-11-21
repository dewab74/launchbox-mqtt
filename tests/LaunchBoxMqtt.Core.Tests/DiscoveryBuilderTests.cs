using System.Text.Json;
using System.Text.RegularExpressions;
using LaunchBoxMqtt.Core.Discovery;
using LaunchBoxMqtt.Core.Payloads;
using LaunchBoxMqtt.Core.State;
using LaunchBoxMqtt.Core.Topics;
using Microsoft.Extensions.Time.Testing;

namespace LaunchBoxMqtt.Core.Tests;

public partial class DiscoveryBuilderTests
{
    private readonly TopicLayout _topics = new("launchbox", "homeassistant", "cocktail");

    private DiscoveryBuilder Builder => new(_topics, "COCKTAIL", "1.2.3");

    [Fact]
    public void One_retained_config_per_entity_on_the_discovery_topic()
    {
        var configs = Builder.BuildConfigs();

        Assert.Equal(DiscoveryBuilder.Entities.Count + DiscoveryBuilder.CommandEntities.Count, configs.Count);
        Assert.All(configs, m => Assert.True(m.Retain));
        Assert.Contains(configs, m => m.Topic == "homeassistant/sensor/launchbox_cocktail/now_playing/config");
        Assert.Contains(configs, m => m.Topic == "homeassistant/binary_sensor/launchbox_cocktail/playing/config");
        Assert.Equal(configs.Count, configs.Select(m => m.Topic).Distinct().Count());
    }

    [Fact]
    public void Every_config_shares_the_device_and_availability()
    {
        foreach (var message in new DiscoveryBuilder(_topics, "COCKTAIL", "1.2.3", commandsEnabled: true).BuildConfigs())
        {
            using var doc = JsonDocument.Parse(message.Payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("command_topic", out var commandTopic))
            {
                Assert.StartsWith("launchbox/cocktail/command/", commandTopic.GetString(), StringComparison.Ordinal);
                Assert.False(root.GetProperty("retain").GetBoolean());
            }
            else
            {
                Assert.Equal("launchbox/cocktail/state", root.GetProperty("state_topic").GetString());
            }

            Assert.Equal("launchbox/cocktail/availability", root.GetProperty("availability_topic").GetString());
            Assert.Equal(Availability.Online, root.GetProperty("payload_available").GetString());
            Assert.Equal(Availability.Offline, root.GetProperty("payload_not_available").GetString());
            Assert.StartsWith("launchbox_cocktail_", root.GetProperty("unique_id").GetString(), StringComparison.Ordinal);

            var device = root.GetProperty("device");
            Assert.Equal("launchbox_cocktail", device.GetProperty("identifiers")[0].GetString());
            Assert.Equal("LaunchBox (COCKTAIL)", device.GetProperty("name").GetString());
            Assert.Equal("1.2.3", device.GetProperty("sw_version").GetString());
            Assert.Equal("launchbox-mqtt", root.GetProperty("origin").GetProperty("name").GetString());
        }
    }

    [Fact]
    public void Unique_ids_are_unique()
    {
        var ids = new DiscoveryBuilder(_topics, "COCKTAIL", "1.2.3", commandsEnabled: true).BuildConfigs()
            .Select(m => JsonElement.Parse(m.Payload).GetProperty("unique_id").GetString())
            .ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Attribute_entities_point_at_the_state_topic()
    {
        var nowPlaying = Builder.BuildConfig(DiscoveryBuilder.Entities.Single(e => e.ObjectId == "now_playing"));
        Assert.Equal("launchbox/cocktail/state", nowPlaying["json_attributes_topic"]!.GetValue<string>());
        Assert.NotNull(nowPlaying["json_attributes_template"]);
    }

    [Fact]
    public void Lock_entity_does_not_use_the_inverted_lock_device_class()
    {
        var locked = Builder.BuildConfig(DiscoveryBuilder.Entities.Single(e => e.ObjectId == "locked"));
        Assert.Null(locked["device_class"]);
    }

    [Fact]
    public void Templates_only_reference_fields_the_state_payload_has()
    {
        var time = new FakeTimeProvider();
        var tracker = new StateTracker(time);
        tracker.GameLaunched(TestGames.Galaga);
        tracker.GameExited();
        tracker.GameLaunched(TestGames.PacMan);
        using var payload = JsonDocument.Parse(StatePayload.Build(tracker.Current));
        var fields = payload.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        foreach (var entity in DiscoveryBuilder.Entities)
        {
            var templates = new[] { entity.ValueTemplate ?? string.Empty, entity.AttributesTemplate ?? string.Empty };
            foreach (Match m in templates.SelectMany(t => ValueJsonField().Matches(t)))
            {
                Assert.Contains(m.Groups[1].Value, fields);
            }
        }
    }

    [Fact]
    public void Command_entities_are_removed_while_commands_are_off()
    {
        var off = Builder.BuildConfigs();
        var on = new DiscoveryBuilder(_topics, "COCKTAIL", "1.2.3", commandsEnabled: true).BuildConfigs();

        var exitTopic = "homeassistant/button/launchbox_cocktail/exit_game/config";
        Assert.Equal(string.Empty, off.Single(m => m.Topic == exitTopic).Payload);
        Assert.True(off.Single(m => m.Topic == exitTopic).Retain);
        Assert.Equal("launchbox/cocktail/command/exit_game", JsonElement.Parse(on.Single(m => m.Topic == exitTopic).Payload).GetProperty("command_topic").GetString());

        var launch = JsonElement.Parse(on.Single(m => m.Topic.Contains("/text/", StringComparison.Ordinal)).Payload);
        Assert.Equal("launchbox/cocktail/command/launch_game", launch.GetProperty("command_topic").GetString());
        Assert.Matches(launch.GetProperty("pattern").GetString()!, "6f1c2a4e-0000-4000-8000-000000000001");
    }

    [Fact]
    public void Removals_empty_every_config_topic()
    {
        var removals = Builder.BuildRemovals();
        Assert.Equal(Builder.BuildConfigs().Select(m => m.Topic), removals.Select(m => m.Topic));
        Assert.All(removals, m =>
        {
            Assert.True(m.Retain);
            Assert.Equal(string.Empty, m.Payload);
        });
    }

    [GeneratedRegex(@"value_json\.([a-z_]+)")]
    private static partial Regex ValueJsonField();
}

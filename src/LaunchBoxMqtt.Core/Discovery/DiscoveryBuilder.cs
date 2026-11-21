using System.Text.Json.Nodes;
using LaunchBoxMqtt.Core.Mqtt;
using LaunchBoxMqtt.Core.Payloads;
using LaunchBoxMqtt.Core.Topics;

namespace LaunchBoxMqtt.Core.Discovery;

/// <summary>
/// Builds the Home Assistant MQTT discovery messages that create one device,
/// "LaunchBox (&lt;PC name&gt;)", with an entity per published value. All of them
/// read the single retained state topic through value templates.
/// </summary>
public sealed class DiscoveryBuilder
{
    public const string ProjectUrl = "https://github.com/dewab74/launchbox-mqtt";

    private readonly TopicLayout _topics;
    private readonly string _displayName;
    private readonly string _version;
    private readonly bool _commandsEnabled;

    public DiscoveryBuilder(TopicLayout topics, string displayName, string version, bool commandsEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        _topics = topics;
        _displayName = displayName;
        _version = version;
        _commandsEnabled = commandsEnabled;
    }

    /// <summary>The entities, in a fixed order. The object id is also the unique-id suffix.</summary>
    public static IReadOnlyList<EntityDefinition> Entities { get; } =
    [
        new("sensor", "now_playing", "Now playing", "{{ value_json.now_playing }}")
        {
            Icon = "mdi:gamepad-variant",
            AttributesTemplate = "{{ (value_json.game or {}) | tojson }}",
        },
        new("binary_sensor", "playing", "Playing", "{{ 'ON' if value_json.playing else 'OFF' }}")
        {
            DeviceClass = "running",
        },
        new("sensor", "platform", "Platform", "{{ value_json.platform if value_json.platform is not none else 'None' }}")
        {
            Icon = "mdi:controller-classic",
        },
        new("binary_sensor", "bigbox_running", "BigBox running", "{{ 'ON' if value_json.bigbox_running else 'OFF' }}")
        {
            DeviceClass = "running",
        },
        new("binary_sensor", "attract_mode", "Attract mode", "{{ 'ON' if value_json.attract_mode else 'OFF' }}")
        {
            Icon = "mdi:television-play",
        },
        // No "lock" device class: in Home Assistant that class means ON = unlocked.
        new("binary_sensor", "locked", "BigBox locked", "{{ 'ON' if value_json.locked else 'OFF' }}")
        {
            Icon = "mdi:lock",
        },
        new("sensor", "last_played", "Last played", "{{ value_json.last_played if value_json.last_played is not none else 'None' }}")
        {
            Icon = "mdi:history",
            AttributesTemplate = "{{ (value_json.last_session or {}) | tojson }}",
        },
        new("sensor", "last_played_at", "Last played at", "{{ value_json.last_played_at if value_json.last_played_at is not none else 'None' }}")
        {
            DeviceClass = "timestamp",
        },
        new("sensor", "last_session_length", "Last session length", "{{ value_json.last_session_seconds if value_json.last_session_seconds is not none else 'None' }}")
        {
            DeviceClass = "duration",
            UnitOfMeasurement = "s",
        },
    ];

    /// <summary>
    /// Command entities, only discovered while commands are enabled. When they
    /// are off, their config topics are emptied so Home Assistant removes them.
    /// </summary>
    public static IReadOnlyList<EntityDefinition> CommandEntities { get; } =
    [
        new("button", "exit_game", "Exit game", ValueTemplate: null)
        {
            Icon = "mdi:exit-run",
            CommandTopicName = Commands.ExitGameCommand.TopicName,
        },
        // A game id in, a launch out; no state of its own (Home Assistant keeps the last value sent).
        new("text", "launch_game", "Launch game (id)", ValueTemplate: null)
        {
            Icon = "mdi:play-box",
            CommandTopicName = Commands.LaunchGameCommand.TopicName,
            Pattern = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        },
    ];

    /// <summary>
    /// The retained config messages that create the device and its entities,
    /// plus either the command entities or empty configs that remove them.
    /// </summary>
    public IReadOnlyList<OutboundMessage> BuildConfigs() =>
        [
            .. Entities.Select(e => new OutboundMessage(_topics.DiscoveryTopic(e.Component, e.ObjectId), BuildConfig(e).ToJsonString(), Retain: true)),
            .. CommandEntities.Select(e => new OutboundMessage(
                _topics.DiscoveryTopic(e.Component, e.ObjectId),
                _commandsEnabled ? BuildConfig(e).ToJsonString() : string.Empty,
                Retain: true)),
        ];

    /// <summary>
    /// Empty retained payloads on every config topic: Home Assistant deletes the
    /// entities and the broker forgets the retained configs. Used on uninstall
    /// and when discovery is switched off.
    /// </summary>
    public IReadOnlyList<OutboundMessage> BuildRemovals() =>
        [.. Entities.Concat(CommandEntities).Select(e => new OutboundMessage(_topics.DiscoveryTopic(e.Component, e.ObjectId), string.Empty, Retain: true))];

    internal JsonObject BuildConfig(EntityDefinition entity)
    {
        var config = new JsonObject
        {
            ["name"] = entity.Name,
            ["unique_id"] = $"{_topics.DeviceId}_{entity.ObjectId}",
            ["availability_topic"] = _topics.AvailabilityTopic,
            ["payload_available"] = Availability.Online,
            ["payload_not_available"] = Availability.Offline,
            ["device"] = new JsonObject
            {
                ["identifiers"] = new JsonArray(_topics.DeviceId),
                ["name"] = $"LaunchBox ({_displayName})",
                ["model"] = "LaunchBox / BigBox",
                ["sw_version"] = _version,
            },
            ["origin"] = new JsonObject
            {
                ["name"] = "launchbox-mqtt",
                ["sw_version"] = _version,
                ["support_url"] = ProjectUrl,
            },
        };

        if (entity.CommandTopicName is { } command)
        {
            config["command_topic"] = _topics.CommandTopic(command);
            config["retain"] = false;
            config["qos"] = 1;
        }
        else
        {
            config["state_topic"] = _topics.StateTopic;
            config["value_template"] = entity.ValueTemplate;
        }

        if (entity.Pattern is { } pattern)
        {
            config["pattern"] = pattern;
            config["min"] = 36;
            config["max"] = 36;
        }

        if (entity.DeviceClass is { } deviceClass)
        {
            config["device_class"] = deviceClass;
        }

        if (entity.Icon is { } icon)
        {
            config["icon"] = icon;
        }

        if (entity.UnitOfMeasurement is { } unit)
        {
            config["unit_of_measurement"] = unit;
        }

        if (entity.AttributesTemplate is { } attributes)
        {
            config["json_attributes_topic"] = _topics.StateTopic;
            config["json_attributes_template"] = attributes;
        }

        return config;
    }
}

/// <summary>One Home Assistant entity, as the discovery config describes it.</summary>
public sealed record EntityDefinition(string Component, string ObjectId, string Name, string? ValueTemplate)
{
    /// <summary>For command entities: the command topic name; they then have no state topic.</summary>
    public string? CommandTopicName { get; init; }

    /// <summary>For text entities: the allowed input.</summary>
    public string? Pattern { get; init; }

    public string? DeviceClass { get; init; }

    public string? Icon { get; init; }

    public string? UnitOfMeasurement { get; init; }

    public string? AttributesTemplate { get; init; }
}

using System.Text;
using LaunchBoxMqtt.Core.Topics;

namespace LaunchBoxMqtt.Core.Commands;

/// <summary>
/// Turns an incoming MQTT message into a <see cref="Command"/>, or says why
/// not. Deliberately strict: anything unexpected is rejected, not guessed at.
/// </summary>
public static class CommandParser
{
    /// <summary>Longer payloads are rejected unread; no valid command needs more.</summary>
    public const int MaxPayloadBytes = 256;

    public static (Command? Command, string? Rejection) Parse(TopicLayout topics, string topic, ReadOnlySpan<byte> payload, bool retained)
    {
        ArgumentNullException.ThrowIfNull(topics);
        ArgumentNullException.ThrowIfNull(topic);
        if (!topic.StartsWith(topics.CommandTopicPrefix, StringComparison.Ordinal))
        {
            return (null, $"not a command topic: {topic}");
        }

        var name = topic[topics.CommandTopicPrefix.Length..];

        // A retained command would run again on every reconnect, so it is never acted on.
        if (retained)
        {
            return (null, $"{name}: retained messages are ignored; publish commands with retain off");
        }

        if (payload.Length > MaxPayloadBytes)
        {
            return (null, $"{name}: payload too long ({payload.Length} bytes)");
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(payload).Trim();
        }
        catch (DecoderFallbackException)
        {
            return (null, $"{name}: payload is not UTF-8");
        }

        switch (name)
        {
            case ExitGameCommand.TopicName:
                return (new ExitGameCommand(), null);

            case LaunchGameCommand.TopicName:
                // LaunchBox game ids are GUIDs; accept the bare id or {"id": "..."}.
                var id = text.StartsWith('{') ? ReadJsonId(text) : text;
                return Guid.TryParseExact(id, "D", out var gameId) && gameId != Guid.Empty
                    ? (new LaunchGameCommand(gameId), null)
                    : (null, $"{name}: payload must be a LaunchBox game id (a GUID)");

            default:
                return (null, $"unknown command '{name}'");
        }
    }

    private static string? ReadJsonId(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == System.Text.Json.JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

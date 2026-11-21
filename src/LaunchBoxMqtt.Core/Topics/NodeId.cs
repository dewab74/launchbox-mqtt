using System.Text;

namespace LaunchBoxMqtt.Core.Topics;

/// <summary>
/// Turns a PC name into an identifier that is safe in MQTT topics and in Home
/// Assistant discovery (<c>[a-z0-9_-]</c>).
/// </summary>
public static class NodeId
{
    public const string Fallback = "launchbox";

    public static string FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fallback;
        }

        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            var safe = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            var next = safe ? c : '_';
            if (next == '_' && sb.Length > 0 && sb[^1] == '_')
            {
                continue;
            }

            sb.Append(next);
        }

        var id = sb.ToString().Trim('_');
        return id.Length == 0 ? Fallback : id;
    }
}

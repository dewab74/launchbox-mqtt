using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Services.Achievements;

namespace LaunchBoxMqtt.Plugin;

/// <summary>
/// A second, tiny entry point whose only job is to receive LaunchBox's
/// achievement service by constructor injection and hand it to
/// <see cref="LaunchBoxGameStats"/>. Kept apart from
/// <see cref="LaunchBoxMqttPlugin"/> on purpose: if a LaunchBox version cannot
/// supply it, only achievements go missing, never the bridge.
/// </summary>
public sealed class GameStatsPlugin : ISystemEventsPlugin
{
    public GameStatsPlugin(IAchievementManager achievements) => LaunchBoxGameStats.Register(achievements);

    public void OnEventRaised(string eventType)
    {
    }
}

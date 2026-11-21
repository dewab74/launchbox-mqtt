using System.Text.Json;
using System.Text.Json.Serialization;
using LaunchBoxMqtt.Core.Bridge;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Capabilities.Activity;
using Unbroken.LaunchBox.Plugins.Capabilities.Leaderboards;
using Unbroken.LaunchBox.Plugins.Data;
using Unbroken.LaunchBox.Plugins.Services.Achievements;
using Unbroken.LaunchBox.Plugins.Services.Activity;
using Unbroken.LaunchBox.Plugins.Services.Leaderboards;

namespace LaunchBoxMqtt.Plugin;

/// <summary>
/// TEMPORARY, read-only research probe: logs what LaunchBox's achievement,
/// leaderboard and player-activity services report on this install, to decide
/// what "game stats" the bridge could publish. Its own entry point, so if
/// LaunchBox cannot supply these services only the probe fails to load, never
/// the bridge. Reads cached data and read-only queries; never refreshes,
/// signs in or submits anything. Remove once the stats design is settled.
/// </summary>
public sealed class StatsProbe(IAchievementManager achievements, ILeaderboardManager leaderboards, IPlayerActivityManager activity) : ISystemEventsPlugin
{
    private static readonly JsonSerializerOptions Json = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter() },
    };

    private static int _ran;

    public void OnEventRaised(string eventType)
    {
        if (eventType is not (SystemEventTypes.BigBoxStartupCompleted or SystemEventTypes.LaunchBoxStartupCompleted)
            || Interlocked.Exchange(ref _ran, 1) == 1)
        {
            return;
        }

        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        try
        {
            var games = PluginHelper.DataManager?.GetAllGames() ?? [];
            var played = games.Where(g => g.LastPlayedDate is not null).OrderByDescending(g => g.LastPlayedDate).ToList();
            var samples = new[]
            {
                played.FirstOrDefault(g => g.Platform == "Arcade"),
                played.FirstOrDefault(g => g.Platform != "Arcade"),
            }.OfType<IGame>().ToList();
            Log($"samples: {string.Join("; ", samples.Select(g => $"{g.Title} [{g.Platform}] id={g.Id} rom={Path.GetFileNameWithoutExtension(g.ApplicationPath)}"))}");

            await Step("achievements.sources", async () => Dump(await achievements.GetSourcesAsync(ct).ConfigureAwait(false))).ConfigureAwait(false);
            foreach (var g in samples)
            {
                await Step($"achievements.cached[{g.Title}]", async () => Dump(await achievements.GetCachedAsync(g, ct).ConfigureAwait(false))).ConfigureAwait(false);
            }

            foreach (var source in await achievements.GetSourcesAsync(ct).ConfigureAwait(false))
            {
                await Step($"achievements.contexts[{source.SourceId}]", async () => Dump(await achievements.GetContextsAsync(source.SourceId, ct).ConfigureAwait(false))).ConfigureAwait(false);
            }

            await Step("leaderboards.sources", async () => Dump(await leaderboards.GetSourcesAsync(ct).ConfigureAwait(false))).ConfigureAwait(false);
            foreach (var source in await leaderboards.GetSourcesAsync(ct).ConfigureAwait(false))
            {
                foreach (var g in samples)
                {
                    foreach (var gameId in new[] { g.Id, Path.GetFileNameWithoutExtension(g.ApplicationPath) }.Distinct())
                    {
                        await Step($"leaderboards.get[{source.SourceId}, {g.Title}, gameId={gameId}]", async () =>
                            Dump(await leaderboards.GetAsync(source.SourceId, new LeaderboardRequest { GameId = gameId }, ct).ConfigureAwait(false))).ConfigureAwait(false);
                    }
                }
            }

            await Step("activity.sources", async () => Dump(await activity.GetSourcesAsync(ct).ConfigureAwait(false))).ConfigureAwait(false);
            foreach (var source in await activity.GetSourcesAsync(ct).ConfigureAwait(false))
            {
                await Step($"activity.get[{source.SourceId}]", async () =>
                    Dump(await activity.GetAsync(source.SourceId, new PlayerActivityRequest { PageSize = 3, RecentAchievementsPerGame = 3 }, ct).ConfigureAwait(false))).ConfigureAwait(false);
            }

            Log("done.");
        }
#pragma warning disable CA1031 // A research probe must never disturb LaunchBox.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log($"aborted: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task Step(string name, Func<Task<string>> read)
    {
        try
        {
            Log($"{name}: {await read().ConfigureAwait(false)}");
        }
#pragma warning disable CA1031 // Record and move on to the next question.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log($"{name}: FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Dump(object? value)
    {
        string text;
        try
        {
            text = JsonSerializer.Serialize(value, Json);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            text = $"(not serializable: {ex.Message}) {value}";
        }

        return text.Length > 3000 ? text[..3000] + $"… ({text.Length} chars)" : text;
    }

    private static void Log(string message) =>
        PluginHost.Context?.Log.Log(BridgeLogLevel.Info, "[stats-probe] " + message);
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using LaunchBoxMqtt.Core.Bridge;
using LaunchBoxMqtt.Core.Stats;
using Unbroken.LaunchBox.Plugins.Data;
using Unbroken.LaunchBox.Plugins.Services.Achievements;

namespace LaunchBoxMqtt.Plugin;

/// <summary>
/// A game's stats from this cabinet: RetroAchievements progress for the account
/// set up in LaunchBox (its achievement manager, with LaunchBox's matching,
/// caching and rate limits) and, for MAME games, the high score table MAME's
/// hiscore plugin keeps on this PC, decoded by the hi2txt that ships with
/// LaunchBox, with the game's factory default entries removed. Never an online
/// leaderboard; read only. The achievement service arrives through
/// <see cref="GameStatsPlugin"/>; without it there are no achievements.
/// </summary>
internal sealed class LaunchBoxGameStats(PluginLog log) : IGameStatsSource
{
    private static readonly TimeSpan Hi2TxtTimeout = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<string, (IGame Game, string? HiFile)> Games = new(StringComparer.OrdinalIgnoreCase);
    private static IAchievementManager? _achievements;

    /// <summary>LaunchBox's install folder (the plugin runs inside <c>&lt;LaunchBox&gt;\Core</c>).</summary>
    private static readonly string LaunchBoxRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));

    /// <summary>Called by <see cref="GameStatsPlugin"/> when LaunchBox constructs it.</summary>
    public static void Register(IAchievementManager? achievements) => Volatile.Write(ref _achievements, achievements);

    /// <summary>Records the game from LaunchBox's launch callbacks (its own thread).</summary>
    public static void Remember(IGame game, IEmulator? emulator)
    {
        string? hiFile = null;
        var isMame = emulator?.Title?.Contains("MAME", StringComparison.OrdinalIgnoreCase) ?? false;
        if (isMame && !string.IsNullOrWhiteSpace(game.ApplicationPath) && !string.IsNullOrWhiteSpace(emulator!.ApplicationPath))
        {
            // MAME's hiscore plugin writes <MAME folder>\hi\<rom>.hi.
            var mameExe = Path.IsPathRooted(emulator.ApplicationPath) ? emulator.ApplicationPath : Path.Combine(LaunchBoxRoot, emulator.ApplicationPath);
            if (Path.GetDirectoryName(mameExe) is { } mameDir)
            {
                hiFile = Path.Combine(mameDir, "hi", Path.GetFileNameWithoutExtension(game.ApplicationPath) + ".hi");
            }
        }

        Games[game.Id] = (game, hiFile);
    }

    public async Task<GameStats?> GetStatsAsync(string gameId, CancellationToken cancellationToken)
    {
        if (!Games.TryGetValue(gameId, out var entry))
        {
            return null;
        }

        var achievements = await ReadAchievementsAsync(entry.Game, cancellationToken).ConfigureAwait(false);
        var highScore = entry.HiFile is { } hi ? await ReadHighScoreAsync(hi, cancellationToken).ConfigureAwait(false) : null;
        return new GameStats(achievements, highScore);
    }

    private async Task<AchievementStats?> ReadAchievementsAsync(IGame game, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _achievements) is not { } manager)
        {
            return null;
        }

        try
        {
            var snapshots = await manager.RefreshAsync(game, cancellationToken).ConfigureAwait(false);
            var best = snapshots
                .SelectMany(s => (s.Progress ?? []).Select(p => (Snapshot: s, Progress: p)))
                .Where(x => (x.Progress.TotalAchievements ?? 0) > 0)
                // The mode with the most unlocked; hardcore wins a tie.
                .OrderByDescending(x => x.Progress.UnlockedAchievements ?? 0)
                .ThenByDescending(x => string.Equals(x.Progress.Context?.ModeId, "hardcore", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (best.Progress is not { } progress)
            {
                var statuses = string.Join(", ", snapshots.Select(s => $"{s.Source?.SourceId}:{s.Status}"));
                log.Log(BridgeLogLevel.Debug, $"No achievement progress for {game.Title} ({(statuses.Length == 0 ? "no source association" : statuses)}).");
                return null;
            }

            // RetroAchievements leaves the point totals empty in the progress summary;
            // the catalog has each achievement's points and the progress which are unlocked.
            var points = (best.Snapshot.Catalog?.Achievements ?? [])
                .Where(a => a.Points is not null && a.AchievementId is not null)
                .ToDictionary(a => a.AchievementId, a => (decimal)a.Points!.Value, StringComparer.Ordinal);
            var unlocked = (progress.Achievements ?? []).Where(a => a.IsUnlocked && a.AchievementId is not null).Select(a => a.AchievementId).ToHashSet(StringComparer.Ordinal);
            var pointsTotal = progress.TotalPoints ?? (points.Count > 0 ? points.Values.Sum() : null);
            var pointsEarned = progress.EarnedPoints ?? (points.Count > 0 ? points.Where(p => unlocked.Contains(p.Key)).Sum(p => p.Value) : null);

            return new AchievementStats(
                best.Snapshot.Source?.DisplayName ?? best.Snapshot.Source?.SourceId ?? "achievements",
                progress.Context?.ModeId,
                progress.UnlockedAchievements ?? 0,
                progress.TotalAchievements ?? 0,
                pointsEarned,
                pointsTotal,
                progress.IsBeaten == true,
                progress.IsCompleted == true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A provider failure means "no achievements", never a broken bridge.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Log(BridgeLogLevel.Warning, $"Achievements for {game.Title} could not be read.", ex);
            return null;
        }
    }

    private async Task<HighScore?> ReadHighScoreAsync(string hiFile, CancellationToken cancellationToken)
    {
        var hi2txt = Path.Combine(LaunchBoxRoot, "ThirdParty", "hi2txt", "hi2txt.exe");
        if (!File.Exists(hiFile))
        {
            log.Log(BridgeLogLevel.Debug, $"No high score file {hiFile} (never played, or no hiscore support).");
            return null;
        }

        if (!File.Exists(hi2txt))
        {
            log.Log(BridgeLogLevel.Warning, $"hi2txt not found at {hi2txt}; high scores unavailable.");
            return null;
        }

        try
        {
            var start = new ProcessStartInfo(hi2txt)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(hi2txt)!,
            };
            start.ArgumentList.Add("-r");
            start.ArgumentList.Add("-xml");
            start.ArgumentList.Add(hiFile);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("hi2txt did not start");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Hi2TxtTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                log.Log(BridgeLogLevel.Warning, $"hi2txt took over {Hi2TxtTimeout.TotalSeconds:0} s on {hiFile}; skipped.");
                return null;
            }

            var entries = HiScoreTable.Parse(await output.ConfigureAwait(false));
            var best = HiScoreTable.Best(entries, ReadDefaults(Path.GetFileNameWithoutExtension(hiFile)));
            if (best is null)
            {
                log.Log(BridgeLogLevel.Debug, $"{Path.GetFileName(hiFile)}: {entries.Count} entries, all factory defaults.");
            }

            return best;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A high score that cannot be read is "none known", never a broken bridge.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            log.Log(BridgeLogLevel.Warning, $"High score in {hiFile} could not be read.", ex);
            return null;
        }
    }

    /// <summary>The game's factory table from hi2txt's defaults archive (empty when it has none).</summary>
    private IReadOnlyList<ScoreEntry> ReadDefaults(string rom)
    {
        var archive = Path.Combine(LaunchBoxRoot, "ThirdParty", "hi2txt", "hi2txt_doc", "hi2txt_defaults.zip");
        try
        {
            if (!File.Exists(archive))
            {
                return [];
            }

            using var zip = ZipFile.OpenRead(archive);
            var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, rom + ".xml", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return [];
            }

            using var reader = new StreamReader(entry.Open());
            return HiScoreTable.Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log.Log(BridgeLogLevel.Debug, $"Defaults for {rom} could not be read: {ex.Message}");
            return [];
        }
    }
}

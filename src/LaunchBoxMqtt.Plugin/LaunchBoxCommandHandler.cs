using System.Diagnostics;
using LaunchBoxMqtt.Core.Bridge;
using LaunchBoxMqtt.Core.Commands;
using LaunchBoxMqtt.Core.State;
using Unbroken.LaunchBox.Plugins;

namespace LaunchBoxMqtt.Plugin;

/// <summary>
/// Carries out MQTT commands inside LaunchBox / Big Box. Only two exist:
/// launch a game that is in the library (by id, through LaunchBox's own
/// PlayGame, never a path or command line), and ask the running game to close.
/// </summary>
internal sealed class LaunchBoxCommandHandler(SynchronizationContext? ui, PluginLog log) : ICommandHandler
{
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    public Task<CommandOutcome> HandleAsync(Command command, CancellationToken cancellationToken) => command switch
    {
        LaunchGameCommand launch => OnUiThreadAsync(() => Launch(launch), launch.Name),
        ExitGameCommand exit => ExitGameAsync(exit, cancellationToken),
        _ => Task.FromResult(CommandOutcome.Rejected(command.Name, "not supported")),
    };

    private static CommandOutcome Launch(LaunchGameCommand command)
    {
        if (PluginHost.Bridge?.Current is { Activity: not PlayState.Idle } current)
        {
            return CommandOutcome.Rejected(command.Name, $"a game is already {current.Activity.ToString().ToLowerInvariant()}: {current.CurrentGame?.Title}");
        }

        var id = command.GameId.ToString("D");
        var game = PluginHelper.DataManager?.GetGameById(id);
        if (game is null)
        {
            return CommandOutcome.Rejected(command.Name, $"no game with id {id} in the LaunchBox library");
        }

        // null additional app, emulator and command line: LaunchBox's normal launch for this game.
        if (PluginHelper.StateManager?.IsBigBox == true && PluginHelper.BigBoxMainViewModel is { } bigBox)
        {
            bigBox.PlayGame(game, null!, null!, null!);
        }
        else if (PluginHelper.LaunchBoxMainViewModel is { } launchBox)
        {
            launchBox.PlayGame(game, null!, null!, null!);
        }
        else
        {
            return CommandOutcome.Rejected(command.Name, "neither Big Box nor LaunchBox is ready to launch games");
        }

        return CommandOutcome.Ok(command.Name, $"launching {game.Title} ({game.Platform})");
    }

    private async Task<CommandOutcome> ExitGameAsync(ExitGameCommand command, CancellationToken cancellationToken)
    {
        if (PluginHost.Bridge?.Current is not { Activity: not PlayState.Idle } current)
        {
            return CommandOutcome.Rejected(command.Name, "no game is running");
        }

        var targets = GameProcesses();
        if (targets.Count == 0)
        {
            return CommandOutcome.Rejected(command.Name, "no game window found among the processes Big Box started");
        }

        foreach (var p in targets)
        {
            log.Log(BridgeLogLevel.Info, $"exit_game: asking {p.ProcessName} ({p.Id}) to close.");
            p.CloseMainWindow();
        }

        // Like Alt+F4: emulators save on a normal close. Never killed; a game that refuses stays running.
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(ExitWait);
        try
        {
            await Task.WhenAll(targets.Select(p => p.WaitForExitAsync(wait.Token))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var still = string.Join(", ", targets.Where(p => !p.HasExited).Select(p => p.ProcessName));
            return CommandOutcome.Rejected(command.Name, $"asked {current.CurrentGame?.Title} to close, but {still} is still running after {ExitWait.TotalSeconds:0} s");
        }
        finally
        {
            foreach (var p in targets)
            {
                p.Dispose();
            }
        }

        return CommandOutcome.Ok(command.Name, $"closed {current.CurrentGame?.Title}");
    }

    /// <summary>
    /// Windowed processes descended from this LaunchBox/Big Box process, minus
    /// LaunchBox's own helpers. Parentage matters: Windows starts unrelated
    /// processes (Game Bar, Edge) when a game launches, and those must never
    /// be closed.
    /// </summary>
    private static List<Process> GameProcesses()
    {
        var self = Environment.ProcessId;
        var launchBoxRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        var helperDirs = new[] { Path.Combine(launchBoxRoot, "Core"), Path.Combine(launchBoxRoot, "System") };
        var parents = NativeMethods.ParentProcessIds();

        bool DescendsFromSelf(int pid)
        {
            for (var depth = 0; depth < 8 && parents.TryGetValue(pid, out var parent) && parent != 0; depth++)
            {
                if (parent == self)
                {
                    return true;
                }

                pid = parent;
            }

            return false;
        }

        var found = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            var keep = false;
            try
            {
                if (p.Id != self && DescendsFromSelf(p.Id) && p.MainWindowHandle != IntPtr.Zero)
                {
                    var path = p.MainModule?.FileName ?? string.Empty;
                    keep = !helperDirs.Any(d => path.StartsWith(d, StringComparison.OrdinalIgnoreCase));
                }
            }
#pragma warning disable CA1031 // Processes come and go, and some deny access; skip those.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            if (keep)
            {
                found.Add(p);
            }
            else
            {
                p.Dispose();
            }
        }

        return found;
    }

    private Task<CommandOutcome> OnUiThreadAsync(Func<CommandOutcome> action, string name)
    {
        if (ui is null)
        {
            return Task.FromResult(CommandOutcome.Rejected(name, "LaunchBox's UI thread is not available to this plugin"));
        }

        var done = new TaskCompletionSource<CommandOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(
            _ =>
            {
                try
                {
                    done.SetResult(action());
                }
#pragma warning disable CA1031 // Reported as the command's outcome.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    log.Log(BridgeLogLevel.Error, $"{name} failed on the UI thread.", ex);
                    done.SetResult(CommandOutcome.Rejected(name, ex.Message));
                }
            },
            null);
        return done.Task;
    }
}

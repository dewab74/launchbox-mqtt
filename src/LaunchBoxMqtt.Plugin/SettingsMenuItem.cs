using LaunchBoxMqtt.Core.Bridge;
using Unbroken.LaunchBox.Plugins;

namespace LaunchBoxMqtt.Plugin;

/// <summary>
/// "LaunchBox MQTT settings…" in LaunchBox's Tools menu. LaunchBox 14.1 shows
/// its built-in plugin settings pages only for catalog plugins with a manifest,
/// so a manifest-less plugin brings its own window.
/// </summary>
public sealed class SettingsMenuItem : ISystemMenuItemPlugin
{
    public string Caption => "LaunchBox MQTT settings…";

    // LaunchBox accepts no icon; the interface is declared non-nullable.
    public Image IconImage => null!;

    public bool ShowInLaunchBox => true;

    // Big Box is driven by a controller; a desktop dialog does not belong there.
    public bool ShowInBigBox => false;

    public bool AllowInBigBoxWhenLocked => false;

    public void OnSelected()
    {
        try
        {
            if (PluginHost.Context is null)
            {
                MessageBox.Show("LaunchBox MQTT is still loading. Try again in a moment.", "LaunchBox MQTT");
                return;
            }

            using var dialog = new SettingsDialog();
            dialog.ShowDialog();
        }
#pragma warning disable CA1031 // A menu callback must never take LaunchBox down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            PluginHost.Context?.Log.Log(BridgeLogLevel.Error, "The settings window failed.", ex);
        }
    }
}

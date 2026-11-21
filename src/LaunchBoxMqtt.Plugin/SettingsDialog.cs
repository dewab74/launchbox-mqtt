using System.Globalization;
using LaunchBoxMqtt.Core.Config;

namespace LaunchBoxMqtt.Plugin;

/// <summary>
/// The settings window, opened from LaunchBox's Tools menu. Plain WinForms
/// laid out in code; all logic lives in <see cref="SettingsActions"/>.
/// </summary>
internal sealed class SettingsDialog : Form
{
    private readonly CheckBox _enabled = new() { Text = "Publish to MQTT", AutoSize = true };
    private readonly TextBox _host = new() { Width = 320 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 100 };
    private readonly CheckBox _tls = new() { Text = "Use TLS", AutoSize = true };
    private readonly TextBox _ca = new() { Width = 260, PlaceholderText = "Blank = Windows' trusted roots" };
    private readonly Button _browse = new() { Text = "Browse…", AutoSize = true };
    private readonly TextBox _username = new() { Width = 320 };
    private readonly TextBox _password = new() { Width = 320, UseSystemPasswordChar = true };
    private readonly TextBox _nodeName = new() { Width = 320 };
    private readonly CheckBox _discovery = new() { Text = "Home Assistant discovery", AutoSize = true };
    private readonly TextBox _discoveryPrefix = new() { Width = 320 };
    private readonly TextBox _baseTopic = new() { Width = 320 };
    private readonly CheckBox _artwork = new() { Text = "Publish game artwork (marquee, else clear logo, else box front)", AutoSize = true };
    private readonly NumericUpDown _artworkWidth = new()
    {
        Minimum = LaunchBoxMqtt.Core.Mqtt.BridgeSettings.MinArtworkMaxWidth,
        Maximum = LaunchBoxMqtt.Core.Mqtt.BridgeSettings.MaxArtworkMaxWidth,
        Increment = 64,
        Width = 100,
    };
    private readonly CheckBox _commands = new() { Text = "Accept commands (exit game, launch game by id)", AutoSize = true };
    private readonly Label _commandsWarning = new()
    {
        Text = "Anyone who can publish to this PC's command topics can then start and close games. Use a broker account for this PC only.",
        AutoSize = true,
        MaximumSize = new System.Drawing.Size(330, 0),
        ForeColor = System.Drawing.SystemColors.GrayText,
    };
    private readonly Button _test = new() { Text = "Test connection", AutoSize = true };
    private readonly Button _save = new() { Text = "Save", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(460, 0) };
    private readonly ErrorProvider _errors = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };
    private readonly Dictionary<string, Control> _fields;
    private readonly CancellationTokenSource _closing = new();

    public SettingsDialog()
    {
        Text = $"LaunchBox MQTT {PluginHost.Version}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AutoScaleMode = AutoScaleMode.Dpi;
        Padding = new Padding(12);
        AcceptButton = _save;
        CancelButton = _cancel;

        _fields = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase)
        {
            [SettingsForm.Enabled] = _enabled,
            [SettingsForm.Host] = _host,
            [SettingsForm.Port] = _port,
            [SettingsForm.Tls] = _tls,
            [SettingsForm.CaCertificatePath] = _ca,
            [SettingsForm.Username] = _username,
            [SettingsForm.Password] = _password,
            [SettingsForm.NodeName] = _nodeName,
            [SettingsForm.DiscoveryEnabled] = _discovery,
            [SettingsForm.DiscoveryPrefix] = _discoveryPrefix,
            [SettingsForm.BaseTopic] = _baseTopic,
            [SettingsForm.CommandsEnabled] = _commands,
            [SettingsForm.ArtworkEnabled] = _artwork,
            [SettingsForm.ArtworkMaxWidth] = _artworkWidth,
        };

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 12, 3) });
            grid.Controls.Add(control);
        }

        void Heading(string text)
        {
            var heading = new Label { Text = text, AutoSize = true, Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold), Margin = new Padding(3, 12, 3, 3) };
            grid.Controls.Add(heading);
            grid.SetColumnSpan(heading, 2);
        }

        var caRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        caRow.Controls.AddRange([_ca, _browse]);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange([_cancel, _save, _test]);

        Heading("Broker");
        Row(string.Empty, _enabled);
        Row("Host", _host);
        Row("Port", _port);
        Row(string.Empty, _tls);
        Row("CA certificate (PEM)", caRow);
        Row("Username", _username);
        Row("Password", _password);
        Heading("Home Assistant");
        Row("Device name", _nodeName);
        Row(string.Empty, _discovery);
        Row("Discovery prefix", _discoveryPrefix);
        Row("Base topic", _baseTopic);
        Row(string.Empty, _artwork);
        Row("Artwork max width (px)", _artworkWidth);
        Heading("Commands");
        Row(string.Empty, _commands);
        Row(string.Empty, _commandsWarning);
        grid.Controls.Add(_status);
        grid.SetColumnSpan(_status, 2);
        grid.Controls.Add(buttons);
        grid.SetColumnSpan(buttons, 2);
        Controls.Add(grid);

        _tls.CheckedChanged += (_, _) => UpdateTlsFields();
        _artwork.CheckedChanged += (_, _) => _artworkWidth.Enabled = _artwork.Checked;
        _browse.Click += (_, _) => BrowseForCa();
        _test.Click += async (_, _) => await RunAsync(SettingsActions.TestAsync, closeOnSuccess: false).ConfigureAwait(true);
        _save.Click += async (_, _) => await RunAsync(SettingsActions.SaveAsync, closeOnSuccess: true).ConfigureAwait(true);
        Load += async (_, _) => await LoadValuesAsync().ConfigureAwait(true);
        FormClosed += (_, _) => _closing.Cancel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing.Dispose();
            _errors.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task LoadValuesAsync()
    {
        SetBusy(true, "Loading…");
        try
        {
            var (values, hasPassword) = await SettingsActions.LoadAsync(_closing.Token).ConfigureAwait(true);
            string V(string key) => values.TryGetValue(key, out var v) ? v : string.Empty;
            _enabled.Checked = V(SettingsForm.Enabled) == "true";
            _host.Text = V(SettingsForm.Host);
            _port.Value = int.TryParse(V(SettingsForm.Port), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 1883;
            _tls.Checked = V(SettingsForm.Tls) == "true";
            _ca.Text = V(SettingsForm.CaCertificatePath);
            _username.Text = V(SettingsForm.Username);
            _password.PlaceholderText = hasPassword ? "Saved. Leave blank to keep it." : "Kept in LaunchBox's secret store";
            _nodeName.Text = V(SettingsForm.NodeName);
            _discovery.Checked = V(SettingsForm.DiscoveryEnabled) != "false";
            _discoveryPrefix.Text = V(SettingsForm.DiscoveryPrefix);
            _baseTopic.Text = V(SettingsForm.BaseTopic);
            _commands.Checked = V(SettingsForm.CommandsEnabled) == "true";
            _artwork.Checked = V(SettingsForm.ArtworkEnabled) == "true";
            _artworkWidth.Value = Math.Clamp(
                int.TryParse(V(SettingsForm.ArtworkMaxWidth), NumberStyles.None, CultureInfo.InvariantCulture, out var w) ? w : LaunchBoxMqtt.Core.Mqtt.BridgeSettings.DefaultArtworkMaxWidth,
                (int)_artworkWidth.Minimum,
                (int)_artworkWidth.Maximum);
            _artworkWidth.Enabled = _artwork.Checked;
            UpdateTlsFields();
            SetBusy(false, string.Empty);
        }
#pragma warning disable CA1031 // Show the problem in the window instead of crashing LaunchBox.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SetBusy(false, $"Could not load the settings: {ex.Message}");
        }
    }

    private async Task RunAsync(Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<SettingsActionResult>> action, bool closeOnSuccess)
    {
        _errors.Clear();
        SetBusy(true, "Working…");
        SettingsActionResult result;
        try
        {
            result = await action(Values(), _closing.Token).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Show the problem in the window instead of crashing LaunchBox.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            result = SettingsActionResult.Fail(ex.Message);
        }

        if (IsDisposed)
        {
            return;
        }

        foreach (var (field, message) in result.FieldErrors)
        {
            if (_fields.TryGetValue(field, out var control))
            {
                _errors.SetError(control, message);
            }
        }

        SetBusy(false, result.Message);
        _status.ForeColor = result.Succeeded ? System.Drawing.Color.DarkGreen : System.Drawing.Color.Firebrick;
        if (result.Succeeded && closeOnSuccess)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private Dictionary<string, string> Values() => new(StringComparer.OrdinalIgnoreCase)
    {
        [SettingsForm.Enabled] = _enabled.Checked ? "true" : "false",
        [SettingsForm.Host] = _host.Text,
        [SettingsForm.Port] = ((int)_port.Value).ToString(CultureInfo.InvariantCulture),
        [SettingsForm.Tls] = _tls.Checked ? "true" : "false",
        [SettingsForm.CaCertificatePath] = _ca.Text,
        [SettingsForm.Username] = _username.Text,
        [SettingsForm.Password] = _password.Text,
        [SettingsForm.NodeName] = _nodeName.Text,
        [SettingsForm.DiscoveryEnabled] = _discovery.Checked ? "true" : "false",
        [SettingsForm.DiscoveryPrefix] = _discoveryPrefix.Text,
        [SettingsForm.BaseTopic] = _baseTopic.Text,
        [SettingsForm.CommandsEnabled] = _commands.Checked ? "true" : "false",
        [SettingsForm.ArtworkEnabled] = _artwork.Checked ? "true" : "false",
        [SettingsForm.ArtworkMaxWidth] = ((int)_artworkWidth.Value).ToString(CultureInfo.InvariantCulture),
    };

    private void UpdateTlsFields()
    {
        _ca.Enabled = _tls.Checked;
        _browse.Enabled = _tls.Checked;
    }

    private void BrowseForCa()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "CA certificate",
            Filter = "Certificates (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|All files (*.*)|*.*",
            FileName = _ca.Text,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _ca.Text = dialog.FileName;
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _test.Enabled = !busy;
        _save.Enabled = !busy;
        _status.ForeColor = System.Drawing.SystemColors.ControlText;
        _status.Text = status;
    }
}

using System.Globalization;

namespace LaunchBoxMqtt.Core.Bridge;

/// <summary>
/// A small size-capped log file in the plugin data directory, so a user can
/// send a log without turning on LaunchBox's debug logging. Keeps one previous
/// file (<c>.1</c>). Never throws: logging must not break the plugin.
/// </summary>
public sealed class FileLog
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    public FileLog(string path, long maxBytes = 1024 * 1024, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _maxBytes = maxBytes;
        _time = time ?? TimeProvider.System;
    }

    public string Path => _path;

    public void Write(BridgeLogLevel level, string message, Exception? exception = null)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{_time.GetUtcNow():yyyy-MM-dd'T'HH:mm:ss.fff'Z'} {level,-7} {message}{(exception is null ? string.Empty : " | " + exception.GetType().Name + ": " + exception.Message)}{Environment.NewLine}");
        lock (_gate)
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > _maxBytes)
                {
                    File.Move(_path, _path + ".1", overwrite: true);
                }

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path))!);
                File.AppendAllText(_path, line);
            }
#pragma warning disable CA1031 // A full disk or locked file must not take the plugin down.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }
}

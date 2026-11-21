using LaunchBoxMqtt.Core.Bridge;

namespace LaunchBoxMqtt.Core.Tests;

public sealed class FileLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lbmqtt-log").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Writes_lines_with_level_and_exception()
    {
        var log = new FileLog(Path.Combine(_dir, "sub", "plugin.log"));
        log.Write(BridgeLogLevel.Info, "hello");
        log.Write(BridgeLogLevel.Error, "boom", new InvalidOperationException("why"));

        var lines = File.ReadAllLines(log.Path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("Info    hello", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("boom | InvalidOperationException: why", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Rotates_when_over_the_cap()
    {
        var log = new FileLog(Path.Combine(_dir, "plugin.log"), maxBytes: 200);
        for (var i = 0; i < 20; i++)
        {
            log.Write(BridgeLogLevel.Info, $"line {i} with some padding to grow the file");
        }

        Assert.True(File.Exists(log.Path + ".1"));
        Assert.True(new FileInfo(log.Path).Length < 400);
    }

    [Fact]
    public void Unwritable_path_does_not_throw()
    {
        var blocker = Path.Combine(_dir, "file");
        File.WriteAllText(blocker, "x");
        var log = new FileLog(Path.Combine(blocker, "cannot", "plugin.log"));
        log.Write(BridgeLogLevel.Info, "ignored");
    }
}

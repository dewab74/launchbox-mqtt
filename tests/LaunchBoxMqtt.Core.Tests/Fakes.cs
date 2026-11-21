using System.Collections.Concurrent;
using LaunchBoxMqtt.Core.Bridge;
using LaunchBoxMqtt.Core.Mqtt;

namespace LaunchBoxMqtt.Core.Tests;

internal sealed class FakeTransport : IMqttTransport
{
    private volatile bool _connected;

    public ConcurrentQueue<OutboundMessage> Published { get; } = new();

    /// <summary>Connect attempts that fail before one succeeds.</summary>
    public int FailConnects { get; set; }

    public int ConnectAttempts { get; private set; }

    /// <summary>When set, every publish waits on it: simulates a hung broker.</summary>
    public TaskCompletionSource? PublishGate { get; set; }

    /// <summary>
    /// When set, the next publish waits on it. If that publish is cancelled it
    /// throws a non-cancellation exception, as MQTTnet does (it reports a
    /// cancelled publish as MqttCommunicationTimedOutException).
    /// </summary>
    public TaskCompletionSource? NextPublishGate { get; set; }

    public int PendingPublishes;

    public bool IsConnected => _connected;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectAttempts++;
        if (FailConnects > 0)
        {
            FailConnects--;
            throw new InvalidOperationException("connection refused (fake)");
        }

        _connected = true;
        return Task.CompletedTask;
    }

    public async Task PublishAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        if (NextPublishGate is { } once)
        {
            NextPublishGate = null;
            Interlocked.Increment(ref PendingPublishes);
            try
            {
                await once.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("The operation has timed out. (fake, like MQTTnet)");
            }
            finally
            {
                Interlocked.Decrement(ref PendingPublishes);
            }
        }

        if (PublishGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (!_connected)
        {
            throw new InvalidOperationException("not connected (fake)");
        }

        Published.Enqueue(message);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        _connected = false;
        return Task.CompletedTask;
    }

    /// <summary>The broker drops the connection.</summary>
    public void Drop() => _connected = false;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingLog : IBridgeLog
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ConcurrentQueue<string> Notifications { get; } = new();

    public void Log(BridgeLogLevel level, string message, Exception? exception = null) =>
        Lines.Enqueue($"{level}: {message}");

    public void NotifyUser(string message) => Notifications.Enqueue(message);
}

internal static class Eventually
{
    public static async Task True(Func<bool> condition, string because, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting: {because}");
            }

            await Task.Delay(10);
        }
    }
}

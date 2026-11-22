using System.Collections.Concurrent;
using LaunchBoxMqtt.Core.Artwork;
using LaunchBoxMqtt.Core.Bridge;
using LaunchBoxMqtt.Core.Mqtt;

namespace LaunchBoxMqtt.Core.Tests;

public sealed class ArtworkTests : IAsyncDisposable
{
    private const string ArtworkTopic = "launchbox/arcade_pc/artwork";

    private readonly FakeTransport _transport = new();
    private readonly RecordingLog _log = new();
    private readonly FakeArtworkSource _source = new();
    private LaunchBoxBridge? _bridge;

    public async ValueTask DisposeAsync()
    {
        if (_bridge is not null)
        {
            await _bridge.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(1200, 400, 800, 800, 267)]
    [InlineData(800, 300, 800, 800, 300)]
    [InlineData(320, 100, 800, 320, 100)]
    [InlineData(5000, 1, 800, 800, 1)]
    public void Sizing_scales_down_only_and_keeps_the_aspect(int w, int h, int max, int expectedW, int expectedH) =>
        Assert.Equal((expectedW, expectedH), ArtworkSizing.Fit(w, h, max));

    [Fact]
    public async Task Launch_publishes_the_artwork_retained_as_binary()
    {
        await StartAsync(artwork: true);
        _bridge!.OnGameLaunching(TestGames.Galaga);
        _bridge.OnGameLaunched(TestGames.Galaga);

        await Eventually.True(() => Artwork().Any(m => m.Binary is { Length: > 0 }), "artwork published");
        var message = Artwork().Single(m => m.Binary is { Length: > 0 });
        Assert.True(message.Retain);
        Assert.Equal(FakeArtworkSource.PngFor(TestGames.Galaga.Id), message.Binary);
        Assert.Equal(1, _source.Requests.Count(r => r == TestGames.Galaga.Id));
        Assert.Equal(640, _source.LastMaxWidth);
    }

    [Fact]
    public async Task Latest_game_wins_when_an_earlier_fetch_is_slow()
    {
        await StartAsync(artwork: true);
        _source.Delays[TestGames.Galaga.Id] = TimeSpan.FromMilliseconds(500);
        _bridge!.OnGameLaunching(TestGames.Galaga);
        _bridge.OnGameExited();
        _bridge.OnGameLaunching(TestGames.PacMan);

        await Eventually.True(() => Artwork().Any(m => m.Binary is { Length: > 0 }), "artwork published");
        await Task.Delay(700, TestContext.Current.CancellationToken);
        Assert.All(Artwork().Where(m => m.Binary is { Length: > 0 }), m => Assert.Equal(FakeArtworkSource.PngFor(TestGames.PacMan.Id), m.Binary));
    }

    [Fact]
    public async Task No_artwork_or_too_large_keeps_the_previous_image()
    {
        await StartAsync(artwork: true);
        _bridge!.OnGameLaunching(TestGames.Galaga);
        await Eventually.True(() => Artwork().Any(m => m.Binary is { Length: > 0 }), "first artwork");

        _source.Missing.Add(TestGames.PacMan.Id);
        _bridge.OnGameExited();
        _bridge.OnGameLaunching(TestGames.PacMan);
        await Eventually.True(() => _log.Lines.Any(l => l.Contains("No artwork for Pac-Man", StringComparison.Ordinal)), "missing logged");

        Assert.Single(Artwork(), m => m.Binary is { Length: > 0 });
    }

    [Fact]
    public async Task Oversized_artwork_is_not_published()
    {
        await StartAsync(artwork: true);
        _source.Oversized = true;
        _bridge!.OnGameLaunching(TestGames.Galaga);
        await Eventually.True(() => _log.Lines.Any(l => l.Contains("not published", StringComparison.Ordinal)), "refusal logged");
        Assert.DoesNotContain(Artwork(), m => m.Binary is { Length: > 0 });
    }

    [Fact]
    public async Task Artwork_off_clears_the_topic_and_never_asks()
    {
        await StartAsync(artwork: false);
        _bridge!.OnGameLaunching(TestGames.Galaga);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Empty(_source.Requests);
        var clear = Assert.Single(Artwork());
        Assert.True(clear.Retain);
        Assert.Null(clear.Binary);
        Assert.Equal(string.Empty, clear.Payload);
        Assert.Contains(_transport.Published, m => m.Topic.EndsWith("/image/launchbox_arcade_pc/artwork/config", StringComparison.Ordinal) && m.Payload.Length == 0);
    }

    [Fact]
    public async Task Reconnect_republishes_the_current_artwork()
    {
        await StartAsync(artwork: true);
        _bridge!.OnGameLaunching(TestGames.Galaga);
        await Eventually.True(() => Artwork().Any(m => m.Binary is { Length: > 0 }), "artwork published");

        _transport.Drop();
        while (_transport.Published.TryDequeue(out _))
        {
        }

        await Eventually.True(() => Artwork().Any(m => m.Binary is { Length: > 0 }), "artwork republished");
        Assert.Single(_source.Requests);
    }

    private async Task StartAsync(bool artwork)
    {
        var settings = new BridgeSettings { Host = "broker.test", NodeName = "Arcade PC", ArtworkEnabled = artwork, ArtworkMaxWidth = 640 };
        var options = new BridgeOptions
        {
            TickInterval = TimeSpan.FromMilliseconds(20),
            BackoffFactory = () => new ReconnectBackoff(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(40)),
        };
        _bridge = new LaunchBoxBridge(settings, _transport, _log, "0.0.0-test", options: options, artwork: _source);
        _bridge.Start();
        await Eventually.True(() => _transport.Published.Any(m => m.Topic == "launchbox/arcade_pc/availability"), "connected");
    }

    private List<OutboundMessage> Artwork() => [.. _transport.Published.Where(m => m.Topic == ArtworkTopic)];

    private sealed class FakeArtworkSource : IArtworkSource
    {
        public ConcurrentQueue<string> Requests { get; } = new();

        public ConcurrentDictionary<string, TimeSpan> Delays { get; } = new();

        public ConcurrentBag<string> Missing { get; } = [];

        public bool Oversized { get; set; }

        public int LastMaxWidth { get; private set; }

        public static byte[] PngFor(string gameId) => System.Text.Encoding.UTF8.GetBytes("PNG:" + gameId);

        public async Task<ArtworkImage?> GetArtworkAsync(string gameId, int maxWidth, CancellationToken cancellationToken)
        {
            Requests.Enqueue(gameId);
            LastMaxWidth = maxWidth;
            if (Delays.TryGetValue(gameId, out var delay))
            {
                await Task.Delay(delay, cancellationToken);
            }

            if (Missing.Contains(gameId))
            {
                return null;
            }

            var png = Oversized ? new byte[LaunchBoxBridge.MaxArtworkBytes + 1] : PngFor(gameId);
            return new ArtworkImage(png, "marquee", 640, 160);
        }
    }
}

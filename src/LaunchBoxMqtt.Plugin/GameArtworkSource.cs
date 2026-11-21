using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LaunchBoxMqtt.Core.Artwork;
using LaunchBoxMqtt.Core.Bridge;
using Unbroken.LaunchBox.Plugins.Data;

namespace LaunchBoxMqtt.Plugin;

/// <summary>
/// A game's artwork from LaunchBox's selected images: marquee, else clear
/// logo, else box front. Paths are read from LaunchBox on its event thread
/// (<see cref="Remember"/>); decoding and scaling happen later on a worker
/// thread, so LaunchBox's API is never called off its own thread.
/// </summary>
internal sealed class GameArtworkSource(PluginLog log) : IArtworkSource
{
    private static readonly ConcurrentDictionary<string, (string Kind, string? Path)[]> Candidates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records the game's image paths. Called from LaunchBox's launch callbacks.</summary>
    public static void Remember(IGame game) =>
        Candidates[game.Id] =
        [
            ("marquee", game.MarqueeImagePath),
            ("clear_logo", game.ClearLogoImagePath),
            ("box_front", game.FrontImagePath),
        ];

    public Task<ArtworkImage?> GetArtworkAsync(string gameId, int maxWidth, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                if (!Candidates.TryGetValue(gameId, out var candidates))
                {
                    return null;
                }

                foreach (var (kind, path) in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    {
                        continue;
                    }

                    try
                    {
                        return Render(path, kind, maxWidth);
                    }
                    catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException or IOException)
                    {
                        // GDI+ reports a corrupt image as OutOfMemory or ArgumentException; try the next kind.
                        log.Log(BridgeLogLevel.Warning, $"Artwork {kind} could not be decoded: {Path.GetFileName(path)}.", ex);
                    }
                }

                return (ArtworkImage?)null;
            },
            cancellationToken);

    private static ArtworkImage Render(string path, string kind, int maxWidth)
    {
        // Read the bytes first: Image.FromFile keeps the file locked until disposed.
        using var input = new MemoryStream(File.ReadAllBytes(path));
        using var source = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
        var (width, height) = ArtworkSizing.Fit(source.Width, source.Height, maxWidth);

        using var scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the edges when scaling
            graphics.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }

        using var output = new MemoryStream();
        scaled.Save(output, ImageFormat.Png);
        return new ArtworkImage(output.ToArray(), kind, width, height);
    }
}

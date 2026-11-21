namespace LaunchBoxMqtt.Core.Artwork;

/// <summary>A game's artwork, ready to publish.</summary>
/// <param name="Png">PNG bytes, already scaled to the configured width.</param>
/// <param name="Kind">Which artwork was used: <c>marquee</c>, <c>clear_logo</c> or <c>box_front</c>.</param>
/// <param name="Width">Published width in pixels.</param>
/// <param name="Height">Published height in pixels.</param>
public sealed record ArtworkImage(byte[] Png, string Kind, int Width, int Height);

/// <summary>
/// Supplies a game's artwork. Implemented by the plugin shell (it knows
/// LaunchBox's image paths and can decode images); called off the UI thread.
/// </summary>
public interface IArtworkSource
{
    /// <summary>The artwork for <paramref name="gameId"/>, or null when the game has none.</summary>
    Task<ArtworkImage?> GetArtworkAsync(string gameId, int maxWidth, CancellationToken cancellationToken);
}

public static class ArtworkSizing
{
    /// <summary>Scales down (never up) to <paramref name="maxWidth"/>, keeping the aspect ratio, at least 1x1.</summary>
    public static (int Width, int Height) Fit(int width, int height, int maxWidth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 1);
        if (width <= maxWidth)
        {
            return (width, height);
        }

        var scaledHeight = (int)Math.Round(height * (double)maxWidth / width);
        return (maxWidth, Math.Max(1, scaledHeight));
    }
}

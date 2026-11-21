namespace LaunchBoxMqtt.Core.State;

/// <summary>
/// The parts of a LaunchBox game that are published. Built by the plugin shell
/// from <c>IGame</c>; Core never sees LaunchBox types.
/// </summary>
public sealed record GameInfo
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? Platform { get; init; }

    public string? Emulator { get; init; }

    public string? Developer { get; init; }

    public string? Publisher { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    public int? ReleaseYear { get; init; }

    public int? PlayCount { get; init; }

    /// <summary>Splits LaunchBox's <c>GenresString</c> ("Shooter; Arcade") into trimmed, distinct genres.</summary>
    public static IReadOnlyList<string> SplitGenres(string? genres) =>
        string.IsNullOrWhiteSpace(genres)
            ? []
            : [.. genres.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];
}

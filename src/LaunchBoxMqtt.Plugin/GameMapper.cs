using LaunchBoxMqtt.Core.State;
using Unbroken.LaunchBox.Plugins.Data;

namespace LaunchBoxMqtt.Plugin;

internal static class GameMapper
{
    public static GameInfo From(IGame game, IAdditionalApplication? app, IEmulator? emulator) => new()
    {
        Id = game.Id,
        Title = game.Title,
        Platform = Blank(game.Platform),
        Emulator = Blank(emulator?.Title),
        Developer = Blank(game.Developer),
        Publisher = Blank(game.Publisher),
        Genres = GameInfo.SplitGenres(game.GenresString),
        ReleaseYear = game.ReleaseYear ?? game.ReleaseDate?.Year,
        PlayCount = app?.PlayCount ?? game.PlayCount,
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

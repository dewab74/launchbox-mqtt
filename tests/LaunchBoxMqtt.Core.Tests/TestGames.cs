using LaunchBoxMqtt.Core.State;

namespace LaunchBoxMqtt.Core.Tests;

internal static class TestGames
{
    public static GameInfo Galaga { get; } = new()
    {
        Id = "6f1c2a4e-0000-4000-8000-000000000001",
        Title = "Galaga",
        Platform = "Arcade",
        Emulator = "MAME",
        Developer = "Namco",
        Publisher = "Midway",
        Genres = ["Shooter"],
        ReleaseYear = 1981,
        PlayCount = 12,
    };

    public static GameInfo PacMan { get; } = new()
    {
        Id = "6f1c2a4e-0000-4000-8000-000000000002",
        Title = "Pac-Man",
        Platform = "Arcade",
    };
}

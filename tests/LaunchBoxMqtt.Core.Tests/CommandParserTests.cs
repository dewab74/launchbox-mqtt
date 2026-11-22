using System.Text;
using LaunchBoxMqtt.Core.Commands;
using LaunchBoxMqtt.Core.Topics;

namespace LaunchBoxMqtt.Core.Tests;

public class CommandParserTests
{
    private const string GameId = "6f1c2a4e-0000-4000-8000-000000000001";
    private readonly TopicLayout _topics = new("launchbox", "homeassistant", "arcade_pc");

    private (Command? Command, string? Rejection) Parse(string name, string payload, bool retained = false) =>
        CommandParser.Parse(_topics, "launchbox/arcade_pc/command/" + name, Encoding.UTF8.GetBytes(payload), retained);

    [Fact]
    public void Exit_game_ignores_its_payload() =>
        Assert.IsType<ExitGameCommand>(Parse("exit_game", "PRESS").Command);

    [Theory]
    [InlineData(GameId)]
    [InlineData("  " + GameId + "\n")]
    [InlineData("{\"id\": \"" + GameId + "\"}")]
    public void Launch_accepts_a_game_id(string payload)
    {
        var command = Assert.IsType<LaunchGameCommand>(Parse("launch_game", payload).Command);
        Assert.Equal(Guid.Parse(GameId), command.GameId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Galaga")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{6f1c2a4e-0000-4000-8000-000000000001}")]
    [InlineData("{\"id\": 42}")]
    [InlineData("{\"path\": \"C:\\\\evil.exe\"}")]
    public void Launch_rejects_anything_but_a_game_id(string payload)
    {
        var (command, rejection) = Parse("launch_game", payload);
        Assert.Null(command);
        Assert.Contains("game id", rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void Retained_commands_are_never_acted_on()
    {
        var (command, rejection) = Parse("launch_game", GameId, retained: true);
        Assert.Null(command);
        Assert.Contains("retained", rejection, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("shutdown")]
    [InlineData("exit_game/extra")]
    [InlineData("")]
    public void Unknown_commands_are_rejected(string name) =>
        Assert.Contains("unknown command", Parse(name, "x").Rejection, StringComparison.Ordinal);

    [Fact]
    public void Other_topics_are_not_commands() =>
        Assert.Null(CommandParser.Parse(_topics, "launchbox/other_pc/command/exit_game", [], false).Command);

    [Fact]
    public void Oversized_and_non_utf8_payloads_are_rejected()
    {
        Assert.Contains("too long", Parse("launch_game", new string('a', 300)).Rejection, StringComparison.Ordinal);
        Assert.Contains("UTF-8", CommandParser.Parse(_topics, "launchbox/arcade_pc/command/launch_game", [0xC3, 0x28], false).Rejection, StringComparison.Ordinal);
    }
}

using LaunchBoxMqtt.Core.Commands;

namespace LaunchBoxMqtt.Core.Tests;

public class GameIdsTests
{
    // Bubble Bobble on the reference cabinet is stored uppercase; most games are lowercase.
    private static readonly Guid BubbleBobble = Guid.Parse("529BB979-F00C-4C02-B82B-A34FD9878510");

    [Fact]
    public void Lookup_tries_lowercase_then_uppercase() =>
        Assert.Equal(
            ["529bb979-f00c-4c02-b82b-a34fd9878510", "529BB979-F00C-4C02-B82B-A34FD9878510"],
            GameIds.LookupCandidates(BubbleBobble));

    [Theory]
    [InlineData("529BB979-F00C-4C02-B82B-A34FD9878510", true)]
    [InlineData("529bb979-f00c-4c02-b82b-a34fd9878510", true)]
    [InlineData("{529BB979-F00C-4C02-B82B-A34FD9878510}", true)]
    [InlineData("529bb979-f00c-4c02-b82b-a34fd9878511", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Stored_ids_match_whatever_their_case(string? stored, bool expected) =>
        Assert.Equal(expected, GameIds.Matches(stored, BubbleBobble));
}

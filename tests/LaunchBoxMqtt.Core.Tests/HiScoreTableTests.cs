using LaunchBoxMqtt.Core.Stats;

namespace LaunchBoxMqtt.Core.Tests;

/// <summary>Real hi2txt output from the reference cabinet (MAME 0.233), 2026-11-21.</summary>
public class HiScoreTableTests
{
    private const string DonkeyKong = """
        <hi2txt>
          <table>
            <col>RANK</col><col>SCORE</col><col>NAME</col>
            <row><cell>1</cell><cell>17200</cell><cell>DEW</cell></row>
            <row><cell>2</cell><cell>12100</cell><cell>DEW</cell></row>
            <row><cell>3</cell><cell>12000</cell><cell>DEW</cell></row>
            <row><cell>4</cell><cell>11200</cell><cell>DEW</cell></row>
            <row><cell>5</cell><cell>10400</cell><cell>   </cell></row>
          </table>
        </hi2txt>
        """;

    private const string DonkeyKongDefaults = """
        <hi2txt>
          <table>
            <col>RANK</col><col>SCORE</col><col>NAME</col>
            <row><cell>1</cell><cell>7650</cell><cell>   </cell></row>
            <row><cell>2</cell><cell>6100</cell><cell>   </cell></row>
            <row><cell>3</cell><cell>5950</cell><cell>   </cell></row>
            <row><cell>4</cell><cell>5050</cell><cell>   </cell></row>
            <row><cell>5</cell><cell>4300</cell><cell>   </cell></row>
          </table>
        </hi2txt>
        """;

    // Never played: the table is exactly the factory one (columns in another order).
    private const string Tempest = """
        <hi2txt>
          <table>
            <col>RANK</col><col>NAME</col><col>SCORE</col>
            <row><cell>1</cell><cell>EJD</cell><cell>10101</cell></row>
            <row><cell>2</cell><cell>DES</cell><cell>10101</cell></row>
            <row><cell>3</cell><cell>RRR</cell><cell>10101</cell></row>
          </table>
        </hi2txt>
        """;

    [Fact]
    public void Parses_columns_by_name()
    {
        var dk = HiScoreTable.Parse(DonkeyKong);
        Assert.Equal(5, dk.Count);
        Assert.Equal(new ScoreEntry(1, 17200m, "DEW"), dk[0]);
        Assert.Null(dk[4].Name);

        var tempest = HiScoreTable.Parse(Tempest);
        Assert.Equal(new ScoreEntry(2, 10101m, "DES"), tempest[1]);
    }

    [Fact]
    public void Best_is_the_cabinets_own_score()
    {
        var best = HiScoreTable.Best(HiScoreTable.Parse(DonkeyKong), HiScoreTable.Parse(DonkeyKongDefaults));
        Assert.NotNull(best);
        Assert.Equal(17200m, best.Score);
        Assert.Equal("DEW", best.Name);
        Assert.Equal(5, best.Entries.Count);
    }

    [Fact]
    public void A_table_of_factory_defaults_means_never_played() =>
        Assert.Null(HiScoreTable.Best(HiScoreTable.Parse(Tempest), HiScoreTable.Parse(Tempest)));

    [Fact]
    public void Only_matching_defaults_are_removed_and_each_only_once()
    {
        ScoreEntry[] defaults = [new(1, 10101m, "EJD"), new(2, 10101m, "DES")];
        ScoreEntry[] table = [new(1, 25000m, "DEW"), new(2, 10101m, "EJD"), new(3, 10101m, "EJD"), new(4, 10101m, "DES")];
        var kept = HiScoreTable.WithoutDefaults(table, defaults);
        Assert.Equal([25000m, 10101m], kept.Select(e => e.Score));
        Assert.Equal("EJD", kept[1].Name); // the second EJD line is a real score that happens to tie
    }

    [Fact]
    public void No_defaults_known_keeps_everything() =>
        Assert.Equal(17200m, HiScoreTable.Best(HiScoreTable.Parse(DonkeyKong), [])!.Score);

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<hi2txt/>")]
    [InlineData("<hi2txt><table><col>RANK</col><col>NAME</col></table></hi2txt>")]
    public void Unreadable_output_is_empty(string xml) => Assert.Empty(HiScoreTable.Parse(xml));

    [Fact]
    public void Grouped_scores_parse()
    {
        var table = HiScoreTable.Parse("<hi2txt><table><col>SCORE</col><col>NAME</col><row><cell>1 234 560</cell><cell>AAA</cell></row><row><cell>--</cell><cell>BBB</cell></row></table></hi2txt>");
        var only = Assert.Single(table);
        Assert.Equal(1234560m, only.Score);
        Assert.Equal(1, only.Rank);
    }
}

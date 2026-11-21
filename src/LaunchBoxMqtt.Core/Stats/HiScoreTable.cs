using System.Globalization;
using System.Xml.Linq;

namespace LaunchBoxMqtt.Core.Stats;

/// <summary>
/// Reads the XML that hi2txt (shipped with LaunchBox) prints for a MAME .hi file,
/// and removes the game's factory default entries, so what is left is what was
/// actually scored on this cabinet.
/// </summary>
public static class HiScoreTable
{
    /// <summary>
    /// The first table's rows. Columns are found by name (RANK, SCORE, NAME); rows
    /// without a readable score are skipped. Anything unreadable gives an empty list.
    /// </summary>
    public static IReadOnlyList<ScoreEntry> Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return [];
        }

        XElement table;
        try
        {
            table = XDocument.Parse(xml).Root?.Element("table") ?? throw new FormatException("no table");
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException)
        {
            return [];
        }

        var columns = table.Elements("col").Select(c => c.Value.Trim().ToUpperInvariant()).ToList();
        var scoreAt = columns.IndexOf("SCORE");
        if (scoreAt < 0)
        {
            return [];
        }

        var nameAt = columns.IndexOf("NAME");
        var rankAt = columns.IndexOf("RANK");
        var entries = new List<ScoreEntry>();
        foreach (var row in table.Elements("row"))
        {
            var cells = row.Elements("cell").Select(c => c.Value).ToList();
            if (scoreAt >= cells.Count || ParseScore(cells[scoreAt]) is not { } score)
            {
                continue;
            }

            var name = nameAt >= 0 && nameAt < cells.Count ? cells[nameAt].Trim() : null;
            var rank = rankAt >= 0 && rankAt < cells.Count && int.TryParse(cells[rankAt].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var r)
                ? r
                : entries.Count + 1;
            entries.Add(new ScoreEntry(rank, score, string.IsNullOrEmpty(name) ? null : name));
        }

        return entries;
    }

    /// <summary>
    /// Removes entries that are the game's factory defaults (same score and name),
    /// each default matching at most one entry.
    /// </summary>
    public static IReadOnlyList<ScoreEntry> WithoutDefaults(IReadOnlyList<ScoreEntry> entries, IReadOnlyList<ScoreEntry> defaults)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(defaults);
        var unused = defaults.Select(Key).ToList();
        var kept = new List<ScoreEntry>();
        foreach (var entry in entries)
        {
            var at = unused.IndexOf(Key(entry));
            if (at >= 0)
            {
                unused.RemoveAt(at);
            }
            else
            {
                kept.Add(entry);
            }
        }

        return kept;
    }

    /// <summary>The cabinet's best, or null when every entry is a factory default (never played).</summary>
    public static HighScore? Best(IReadOnlyList<ScoreEntry> entries, IReadOnlyList<ScoreEntry> defaults, int keep = 5)
    {
        var mine = WithoutDefaults(entries, defaults).OrderByDescending(e => e.Score).ThenBy(e => e.Rank).ToList();
        return mine.Count == 0 ? null : new HighScore(mine[0].Score, mine[0].Name, [.. mine.Take(keep)]);
    }

    private static string Key(ScoreEntry e) =>
        e.Score.ToString(CultureInfo.InvariantCulture) + "|" + (e.Name ?? string.Empty).Trim();

    // Scores can carry grouping ("12 300", "12,300") or a leading zero pad.
    private static decimal? ParseScore(string text)
    {
        var digits = new string([.. text.Where(char.IsAsciiDigit)]);
        return digits.Length is > 0 and <= 28 ? decimal.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture) : null;
    }
}

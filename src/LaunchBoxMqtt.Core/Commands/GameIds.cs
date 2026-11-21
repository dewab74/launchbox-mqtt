namespace LaunchBoxMqtt.Core.Commands;

/// <summary>
/// LaunchBox stores game ids as GUID strings, but not always in the same
/// form: most are lowercase, some (older imports) uppercase, and its
/// GetGameById compares them case-sensitively.
/// </summary>
public static class GameIds
{
    /// <summary>The exact strings to try with a case-sensitive lookup, most likely first.</summary>
    public static IReadOnlyList<string> LookupCandidates(Guid id)
    {
        var lower = id.ToString("D");
        return [lower, lower.ToUpperInvariant()];
    }

    /// <summary>Whether a stored LaunchBox id denotes <paramref name="id"/>, whatever its case or braces.</summary>
    public static bool Matches(string? storedId, Guid id) =>
        Guid.TryParse(storedId, out var parsed) && parsed == id;
}

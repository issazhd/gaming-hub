using System.Text.RegularExpressions;

namespace GamingHub.Core.Games;

/// <summary>
/// Conservative, word-based game-title matching for artwork lookup. Two titles match when they share their
/// real words and differ only by edition words ("Director's Cut", "Anniversary Edition", "DC", "Enhanced"…).
/// Roman numerals equal digits (IV = 4) and a few common abbreviations expand. A different number or any other
/// extra word means a different game ("Alan Wake" ≠ "Alan Wake 2", "Control" ≠ "CONTROL Resonant").
/// </summary>
public static partial class TitleMatch
{
    static readonly HashSet<string> EditionWords =
    [
        "edition", "enhanced", "remastered", "remaster", "definitive", "complete", "ultimate", "deluxe", "gold", "goty",
        "game", "year", "directors", "director", "cut", "anniversary", "standard", "premium", "digital", "special",
        "collectors", "hd", "version", "the",
    ];

    static readonly Dictionary<string, string[]> Abbreviations = new()
    {
        ["dc"] = ["directors", "cut"], ["woa"] = ["world", "of", "assassination"], ["goty"] = ["game", "of", "the", "year"],
    };

    static readonly Dictionary<string, string> Roman = new()
    { ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9", ["x"] = "10" };

    /// <summary>Lower-case letters and digits only ("Tom Clancy's Rainbow Six® Siege X" → "tomclancysrainbowsixsiegex").</summary>
    public static string Normalize(string s) => NonAlnum().Replace(s.ToLowerInvariant(), "");

    public static IReadOnlySet<string> Words(string s)
    {
        var set = new HashSet<string>();
        foreach (var raw in NonAlnum().Split(Apostrophes().Replace(s.ToLowerInvariant(), "")))
        {
            if (raw.Length == 0) continue;
            if (Abbreviations.TryGetValue(raw, out var expanded)) set.UnionWith(expanded);
            else set.Add(Roman.GetValueOrDefault(raw, raw));
        }
        return set;
    }

    public static bool SameGame(string a, string b)
    {
        var (x, y) = (Words(a), Words(b));
        if (!x.Overlaps(y.Where(w => !EditionWords.Contains(w)))) return false;   // must share a real word
        return x.Except(y).Concat(y.Except(x)).All(EditionWords.Contains);
    }

    /// <summary>Search text for store/wiki search: drops "+ mod" suffixes and punctuation that confuse search engines.</summary>
    public static string SearchTerm(string name)
    {
        var t = name.Split(" + ")[0];
        t = Regex.Replace(t, @"[™®:\-–_]", " ");
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    /// <summary>Second-chance search text: abbreviations expanded, edition words dropped
    /// ("Ghost of Tsushima DC" → "Ghost of Tsushima", "HITMAN - WoA" → "HITMAN world of assassination").</summary>
    public static string CoreTerm(string name)
    {
        var words = SearchTerm(name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(w => Abbreviations.TryGetValue(w.ToLowerInvariant(), out var e) ? e : [w])
            .Where(w => !EditionWords.Contains(Apostrophes().Replace(w.ToLowerInvariant(), "")));
        return string.Join(' ', words);
    }

    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NonAlnum();
    [GeneratedRegex("['’`]")] private static partial Regex Apostrophes();
}

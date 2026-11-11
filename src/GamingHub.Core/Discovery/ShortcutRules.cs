using System.Text.RegularExpressions;

namespace GamingHub.Core.Discovery;

/// <summary>Turns a shortcut in a user's "games" folder into a library entry: skips launchers/tools,
/// cleans the display name, and guesses the game's root folder (for tracking and artwork).</summary>
public static partial class ShortcutRules
{
    /// <summary>Store launchers, controller/peripheral tools and utilities that people keep next to games.</summary>
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "epicgameslauncher.exe", "ealauncher.exe", "eadesktop.exe", "origin.exe", "battle.net launcher.exe", "battle.net.exe",
        "ubisoftconnect.exe", "upc.exe", "launcherpatcher.exe", "rockstarservice.exe", "goggalaxy.exe", "galaxyclient.exe",
        "lghub.exe", "lghub_system_tray.exe", "playstationaccessories.exe", "ps5upload-desktop.exe", "ds4windows.exe", "dsx.exe", "zerksense5.exe", "dualpulse.exe",
        "msiafterburner.exe", "rtss.exe", "discord.exe", "parsecd.exe", "gaminghub.exe",
    };

    /// <summary>Sub-folders that hold a game's binaries rather than name the game.</summary>
    static readonly HashSet<string> BinaryDirs = new(StringComparer.OrdinalIgnoreCase)
    { "bin", "bin32", "bin64", "x64", "x86", "win64", "win32", "binaries", "retail", "game", "release", "shipping" };

    public static bool IsNotAGame(string target) =>
        NotGames.Contains(Path.GetFileName(target))
        || target.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)      // Steam library: the Steam scan owns these
        || target.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase);    // Epic install root / launcher

    /// <summary>Steam and Epic links are never taken from shortcuts — those stores are scanned directly (no duplicates).</summary>
    public static bool IsStoreUri(string uri) =>
        uri.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("com.epicgames.launcher:", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Play Samson" → "Samson"; "AlanWake2 - Shortcut" (exe-like) → the game folder's name.</summary>
    public static string Name(string shortcutFile, string? target)
    {
        var n = Path.GetFileNameWithoutExtension(shortcutFile);
        n = ShortcutSuffix().Replace(n, "");
        n = Regex.Replace(n, @"\.exe$", "", RegexOptions.IgnoreCase);
        n = Regex.Replace(n, @"^Play\s+", "", RegexOptions.IgnoreCase).Trim();
        if (target is not null && LooksLikeExeName(n, target) && GameRoot(target) is { } root) return Path.GetFileName(root);
        return n;
    }

    static bool LooksLikeExeName(string n, string target)
    {
        if (n.Contains(' ')) return false;
        var stem = Path.GetFileNameWithoutExtension(target);
        return n.Length <= 3 || n.Equals(stem, StringComparison.OrdinalIgnoreCase) || n == n.ToLowerInvariant();
    }

    /// <summary>Walks up from the executable past binary folders: …\Cyberpunk 2077\bin\x64\x.exe → …\Cyberpunk 2077.</summary>
    public static string? GameRoot(string target)
    {
        var dir = Path.GetDirectoryName(target);
        while (dir is not null && BinaryDirs.Contains(Path.GetFileName(dir))) dir = Path.GetDirectoryName(dir);
        if (dir is null || Path.GetDirectoryName(dir) is null) return null;   // never a drive root
        // "…\007 First Light\007 First Light\Retail" style double folders collapse to the outer one.
        var parent = Path.GetDirectoryName(dir);
        if (parent is not null && Path.GetFileName(parent).Equals(Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase)) dir = parent;
        return dir;
    }

    [GeneratedRegex(@"\s*-\s*(Shortcut|Raccourci|Verknüpfung)$", RegexOptions.IgnoreCase)] private static partial Regex ShortcutSuffix();
}

using GamingHub.Core.Displays;

namespace GamingHub.Core.Games;

public enum GameTargetMode { FollowLauncher, WindowsPrimary, SpecificDisplay, LastUsed }
public enum GameTargetFallback { FollowLauncher, WindowsPrimary, Ask }

public sealed class Game
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Source { get; set; } = "manual";
    public string? ExternalId { get; set; }
    public string? LaunchUri { get; set; }
    public string? InstallDir { get; set; }
    public bool Installed { get; set; } = true;
    public bool AutoPlace { get; set; } = true;
    public bool Hidden { get; set; }
    /// <summary>Start the controller helper (ZerkSense5 or DS4Windows) before the game. Stored in the "ds4windows" column.</summary>
    public bool UseControllerHelper { get; set; }
    /// <summary>The controller helper selected for this game. Older enabled entries without a name are migrated at startup.</summary>
    public string? HelperName { get; set; }
    /// <summary>Listed in the Home row — the user picks these (Library always lists every game).</summary>
    public bool ShowOnHome { get; set; }
    /// <summary>User-chosen program to launch instead of the store link / discovered executable (kept across rescans).</summary>
    public string? CustomExe { get; set; }
    /// <summary>Position on Home when the user arranges Home themselves.</summary>
    public int HomeOrder { get; set; }
    /// <summary>Start through a Gaming Hub scheduled task with admin rights, so there is no UAC prompt (set up once, with approval).</summary>
    public bool RunElevated { get; set; }
    /// <summary>The quick menu may pause (suspend) this game while it is open. Off by default — never for online/anti-cheat games.</summary>
    public bool AllowPause { get; set; }
    public string ExecutablePath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public string? CoverPath { get; set; }
    public string? BackgroundPath { get; set; }
    public string Categories { get; set; } = "";
    public bool Favorite { get; set; }
    public GameTargetMode TargetMode { get; set; } = GameTargetMode.FollowLauncher;
    public DisplayIdentity? TargetDisplay { get; set; }
    public DisplayIdentity? LastUsedDisplay { get; set; }
    public GameTargetFallback TargetFallback { get; set; } = GameTargetFallback.FollowLauncher;
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastPlayedUtc { get; set; }
    public long PlaytimeSeconds { get; set; }
}

public interface IGameRepository
{
    IReadOnlyList<Game> GetAll();
    Game Save(Game game);
    void Delete(long id);
    void AddPlaytime(long id, TimeSpan played, DateTime endedUtc);
}

public sealed record LaunchCommand(string FileName, string Arguments, string WorkingDirectory);

public static class LaunchCommandBuilder
{
    public static LaunchCommand Build(Game g)
    {
        if (!string.IsNullOrWhiteSpace(g.CustomExe))   // user override wins
        {
            var custom = g.CustomExe.Trim().Trim('"');
            return new(custom, g.Arguments?.Trim() ?? "", Path.GetDirectoryName(custom) ?? "");
        }
        // Platform games launch through their launcher's protocol so DRM/overlays/cloud saves work.
        if (!string.IsNullOrWhiteSpace(g.LaunchUri)) return new(g.LaunchUri!, "", "");
        if (string.IsNullOrWhiteSpace(g.ExecutablePath))
            throw new InvalidOperationException($"'{g.Name}' has no executable path.");
        var exe = g.ExecutablePath.Trim().Trim('"');
        var wd = string.IsNullOrWhiteSpace(g.WorkingDirectory)
            ? Path.GetDirectoryName(exe) ?? ""
            : g.WorkingDirectory!.Trim().Trim('"');
        return new(exe, g.Arguments?.Trim() ?? "", wd);
    }
}

public sealed record GameTargetResult(DisplayInfo? Display, bool AskUser, string Reason);

/// <summary>Resolves which display a game should launch on (placement itself is Phase 4).</summary>
public static class GameTargetResolver
{
    public static GameTargetResult Resolve(Game g, DisplaySnapshot snap, DisplayInfo? launcherDisplay)
    {
        var active = snap.Active.ToList();
        DisplayInfo? FollowLauncher() =>
            launcherDisplay is not null ? DisplayMatcher.Match(launcherDisplay.Identity, active).Display : null;

        DisplayInfo? chosen = g.TargetMode switch
        {
            GameTargetMode.FollowLauncher => FollowLauncher(),
            GameTargetMode.WindowsPrimary => snap.Primary,
            GameTargetMode.SpecificDisplay => DisplayMatcher.Match(g.TargetDisplay, active).Display,
            GameTargetMode.LastUsed => DisplayMatcher.Match(g.LastUsedDisplay, active).Display,
            _ => null,
        };
        if (chosen is not null) return new(chosen, false, g.TargetMode.ToString());

        return g.TargetFallback switch
        {
            GameTargetFallback.Ask => new(null, true, $"{g.TargetMode} unavailable; ask user"),
            GameTargetFallback.WindowsPrimary => new(snap.Primary, false, "fallback: Windows primary"),
            _ => new(FollowLauncher() ?? snap.Primary, false, "fallback: follow launcher"),
        };
    }
}

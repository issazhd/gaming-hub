using GamingHub.Core.Displays;

namespace GamingHub.Core.Settings;

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public LauncherDisplaySettings Display { get; set; } = new();
    public ControllerSettings Controller { get; set; } = new();
    public bool ReducedMotion { get; set; }
    /// <summary>Soft sounds when moving, selecting and going back with the controller.</summary>
    public bool NavigationSounds { get; set; } = true;
    /// <summary>Hide the mouse pointer as soon as the controller is used (moving the mouse shows it again).</summary>
    public bool HidePointerWithController { get; set; } = true;
    /// <summary>Download missing cover/background art from the Steam store for exact title matches.</summary>
    public bool AutoArtwork { get; set; } = true;
    /// <summary>One-time switch to a user-picked Home row (all games were cleared from Home once).</summary>
    public bool HomeCurated { get; set; }
    /// <summary>Send sound to the TV's own output while Gaming Hub is on it; restore the previous device afterwards.</summary>
    public bool AutoAudioSwitch { get; set; } = true;
    /// <summary>Playback device to restore when Gaming Hub leaves the TV (set by automatic audio switching).</summary>
    public string? AudioRestoreId { get; set; }
    /// <summary>Maximum games shown in the Home row (most recently played first).</summary>
    public int HomeMaxGames { get; set; } = 16;
    /// <summary>Home order: false = most recently played first, true = the user's own arrangement.</summary>
    public bool HomeCustomOrder { get; set; }
    /// <summary>Folders of game shortcuts to add to the library. Null = not configured yet (DesktopGames is used if it exists).</summary>
    public List<string>? GameFolders { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> EffectiveGameFolders => GameFolders
        ?? [.. new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Games") }.Where(Directory.Exists)];
    public bool StartWithWindows { get; set; }
    public bool StartMinimizedToTray { get; set; }
    public bool ConsoleMode { get; set; }
    public bool FirstRunCompleted { get; set; }
    public DateTime? LastScanUtc { get; set; }
}

public enum LightBarColor { Blue, Cyan, Green, Amber, Red, Pink, Purple, White, Off, Original }

public sealed class ControllerSettings
{
    public LightBarColor ControllerOneLightBar { get; set; } = LightBarColor.Original;
    public LightBarColor PlayerTwoLightBar { get; set; } = LightBarColor.Red;
    public bool Enabled { get; set; } = true;
    public bool Vibration { get; set; } = true;
    public float StickDeadZone { get; set; } = 0.45f;
    /// <summary>PS button brings Gaming Hub forward from the desktop or other apps.</summary>
    public bool PsButtonOpensHub { get; set; } = true;
    /// <summary>Also while a fullscreen game is in front. Off by default: never hijack games.</summary>
    public bool PsButtonOverGames { get; set; }
    /// <summary>PS during a game opens the quick menu (on the game's screen, or another screen for exclusive fullscreen).</summary>
    public bool QuickMenuOverGames { get; set; } = true;
    /// <summary>DS4Windows.exe location (blank = detect automatically).</summary>
    public string? Ds4WindowsPath { get; set; }
    /// <summary>ZerkSense5.exe location (blank = detect automatically).</summary>
    public string? ZerkSense5Path { get; set; }
    public string? Ds4NexusPath { get; set; }
    /// <summary>Controller helper for flagged games: "ZerkSense5" or "DS4Windows" (blank = ZerkSense5 if installed).</summary>
    public string? Helper { get; set; }
    /// <summary>Triple-press PS: if the preferred display is connected but not part of the desktop, extend the desktop to it (Win+P › Extend). Off by default — it changes the Windows display layout.</summary>
    public bool PsDoubleExtendsDisplay { get; set; }
    /// <summary>DualSense touchpad moves the pointer and its press clicks (Gaming Hub's windows only).</summary>
    public bool TouchpadPointer { get; set; } = true;
    /// <summary>Touchpad pointer speed, 1 (slow) to 5 (very fast).</summary>
    public int TouchpadSpeed { get; set; } = 3;
    /// <summary>Send the touchpad pointer through the FakerInput driver when installed (works on UAC prompts and admin windows).</summary>
    public bool UseFakerInput { get; set; } = true;
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

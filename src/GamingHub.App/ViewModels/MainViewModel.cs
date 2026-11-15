using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GamingHub.App.Services;
using GamingHub.Core.Displays;
using GamingHub.Core.Games;
using GamingHub.Core.Settings;
using GamingHub.Infrastructure.Audio;
using GamingHub.Infrastructure.Games;
using Microsoft.Win32;

namespace GamingHub.App.ViewModels;

public sealed record DisplayItem(DisplayInfo Info, bool IsPreferred, bool IsFallback, bool IsHost)
{
    public string Title => $"{Info.Identity.FriendlyName}{(Info.IsPrimary ? "  (Windows primary)" : "")}";
    public string Details => Info.IsActive
        ? $"{Info.Manufacturer} · {Info.Resolution} · {Info.ScalePercent}% · {Info.OutputTechnology} · {Info.GdiName}"
        : $"{Info.Manufacturer} · {Info.OutputTechnology} · connected but not part of the desktop (enable it in Windows Display settings)";
    public string ConnectionLabel => CanUse ? "On desktop" : "Connected · not extended";
    public string IdentityText => $"ID {Info.Identity.HardwareId} · SN {(Info.Identity.HasEdidSerial ? Info.Identity.EdidSerialString ?? Info.Identity.EdidSerialNumber.ToString() : "n/a")}";
    public bool CanUse => Info.IsActive;
    /// <summary>Part of the desktop but not the Windows main display: "Make main display" is offered.</summary>
    public bool CanMakeMain => Info.IsActive && !Info.IsPrimary;
}

/// <summary>A Settings page in the category rail (Segoe Fluent Icons glyph).</summary>
public sealed record SettingsCategory(string Name, string Glyph);

public sealed record GameTile(Game G)
{
    public string Name => G.Name;
    public string? Cover => G.CoverPath;
    public string? Hero => G.BackgroundPath ?? G.CoverPath;
    public string Initial => G.Name.Length > 0 ? G.Name[..1] : "?";
    public string LastPlayedText => G.LastPlayedUtc is { } lp ? lp.ToLocalTime().ToString("d MMM yyyy") : "Not yet";
    public string SourceText => SourceName(G.Source);

    /// <summary>Time played through Gaming Hub (sessions it launched and saw running).</summary>
    public string PlaytimeText => G.PlaytimeSeconds switch
    {
        0 => "Not played yet",
        < 3600 => $"{Math.Max(1, G.PlaytimeSeconds / 60)} min played",
        _ => $"{G.PlaytimeSeconds / 3600.0:0.#} h played",
    };
    public string Subtitle => string.Join("  ·  ", new[]
    {
        SourceName(G.Source),
        G.PlaytimeSeconds > 0 ? PlaytimeText : null,
        G.LastPlayedUtc is { } lp ? $"last played {lp.ToLocalTime():d}" : null,
        G.Favorite ? "★ Favorite" : null,
    }.Where(s => s is not null));
    public static string SourceName(string s) => s switch { "steam" => "Steam", "epic" => "Epic Games", "gog" => "GOG", "ubisoft" => "Ubisoft Connect", "shortcut" => "Games folder", _ => "Added manually" };
}

/// <summary>A per-game display target choice: a mode, or a specific display.</summary>
public sealed record TargetOption(string Label, GameTargetMode Mode, DisplayIdentity? Display)
{
    public override string ToString() => Label;
}

public sealed partial class MainViewModel : ObservableObject
{
    readonly IGameRepository _repo;
    readonly SettingsService _settings;
    readonly LauncherPlacementService _placement;
    readonly GameLaunchService _launcher;
    readonly DisplayWatcher _watcher;
    readonly LibraryService _library;
    readonly ArtworkService _artwork;
    readonly ControllerHelperService _helper;
    readonly SystemService _system;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };
    readonly DispatcherTimer _testTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    const int TestSeconds = 15;
    List<Game> _all = [];

    public MainViewModel(IGameRepository repo, SettingsService settings, LauncherPlacementService placement, GameLaunchService launcher,
        DisplayWatcher watcher, LibraryService library, SystemService system, ArtworkService artwork, ControllerHelperService helper)
    {
        (_repo, _settings, _placement, _launcher, _watcher, _library, _system, _artwork, _helper) = (repo, settings, placement, launcher, watcher, library, system, artwork, helper);
        helper.MigrateLegacyAssignments(repo);
        _placement.StateChanged += RefreshDisplays;
        _placement.Notify += ShowToast;
        _placement.ExtendedToPreferred += d => System.Windows.Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            if (!DisplaySettings.MakePreferredMainOnExtend || d.GdiName is not { } gdi) return;
            await Task.Delay(1500);   // let Windows finish bringing the display up
            bool hadFocus = _placement.IsHubActive;
            var err = Infrastructure.Displays.DisplayTopology.SetMain(gdi);
            if (err is null && hadFocus) _placement.GoToPreferredSoon(restoreFocus: true, notifyExtended: false);
            Serilog.Log.Information("Make {Display} the main display after extending: {Result}", d.Identity, err ?? "ok");
            ShowToast(err is null ? $"{d.Identity.FriendlyName} is now the main display" : $"Couldn't make {d.Identity.FriendlyName} the main display: {err}");
            _watcher.Schedule("main display changed");
        });
        _launcher.Notify += ShowToast;
        _launcher.SessionChanged += () => { OnPropertyChanged(nameof(RunningName)); OnPropertyChanged(nameof(IsGameRunning)); if (!IsGameRunning) Launching = null; };
        _launcher.LaunchSettled += () => Launching = null;
        _launcher.Exited += _ => ReloadGames();
        _clock.Tick += (_, _) => OnPropertyChanged(nameof(Clock));
        _clock.Start();
        _testTimer.Tick += (_, _) => { if (--TestCountdown <= 0) EndTest(false); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast = null; };
        IsFirstRun = !settings.Current.FirstRunCompleted;
        SettingsSection = SettingsCategories[0];
        settings.Current.StartWithWindows = system.IsStartupRegistered();   // the installer may have set (or not set) it
        if (!settings.Current.HomeCurated)   // Home now holds only games the user adds
        {
            foreach (var g in repo.GetAll().Where(g => g.ShowOnHome)) { g.ShowOnHome = false; repo.Save(g); }
            settings.Current.HomeCurated = true;
            settings.Save();
        }
        ReloadGames();
        _folderDebounce.Tick += async (_, _) => { _folderDebounce.Stop(); await Rescan(quiet: true); };
        WatchGameFolders();
    }

    /// <summary>Background: fetch art for games that have none (each game is tried once).</summary>
    async Task FillArtworkAsync()
    {
        if (!_settings.Current.AutoArtwork) return;
        int n = await Task.Run(() => _artwork.FillMissingAsync(_repo));
        if (n == 0) return;
        ReloadGames();
        ShowToast($"Found artwork for {n} game{(n == 1 ? "" : "s")}");
    }

    [RelayCommand]
    async Task FindArtwork()
    {
        if (string.IsNullOrWhiteSpace(NewName)) { ShowToast("Type the game's name first, then Find art automatically"); return; }
        ShowToast($"Looking for artwork for {NewName}…");
        var g = _editId == 0 ? null : _repo.GetAll().FirstOrDefault(x => x.Id == _editId);
        var r = await _artwork.FindAsync(NewName, g?.Source == "steam" ? g.ExternalId : null);
        if (r is null) { ShowToast($"\"{NewName}\" wasn't found on Steam or PCGamingWiki. Check the name spelling, or use Search images online."); return; }
        if (r.Cover is not null) NewCover = r.Cover;
        if (r.Background is not null) NewBackground = r.Background;
        ShowToast($"Artwork found: {r.MatchedName}. Press Save to keep it.");
    }

    [RelayCommand]
    void SearchArtwork()
    {
        if (ImageSearchWindow.Current is { } open) { open.Activate(); return; }
        new ImageSearchWindow(string.IsNullOrWhiteSpace(NewName) ? "game" : NewName, (kind, path) =>
        {
            if (kind == "cover") NewCover = path; else NewBackground = path;
        }) { Owner = System.Windows.Application.Current.MainWindow }.Show();
    }

    /// <summary>Version shown at the bottom of Settings, e.g. "1.0.2" (build hash stripped).</summary>
    /// <summary>Development build: shows a DEV badge (separate data, never touches start-with-Windows).</summary>
    public bool IsDev => App.IsDev;
    public string AppVersion { get; } = (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(MainViewModel).Assembly)?.InformationalVersion ?? "?").Split('+')[0];

    public LauncherDisplaySettings DisplaySettings => _settings.Current.Display;
    public ControllerSettings ControllerSettings => _settings.Current.Controller;
    public AppSettings AppSettings => _settings.Current;
    public string Clock => DateTime.Now.ToString("t");
    public ObservableCollection<GameTile> HomeGames { get; } = [];
    public ObservableCollection<GameTile> LibraryGames { get; } = [];
    public ObservableCollection<DisplayItem> Displays { get; } = [];
    public ObservableCollection<string> Filters { get; } = [];
    public string[] Sorts { get; } = ["Name", "Recently played", "Most played", "Recently added"];

    // ---- Pages ----
    public enum Page { Home, Library, Settings }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsHome), nameof(IsLibrary), nameof(IsSettings))] Page _currentPage;
    public bool IsHome => CurrentPage == Page.Home;
    public bool IsLibrary => CurrentPage == Page.Library;
    public bool IsSettings => CurrentPage == Page.Settings;

    [ObservableProperty] string _controllerStatus = "No controller";
    /// <summary>Last input came from a controller (button hints show ✕ ○…) rather than keyboard/mouse.</summary>
    [ObservableProperty] bool _usingController = true;
    /// <summary>The Controls list (Create button / F4) is showing.</summary>
    [ObservableProperty] bool _showControls;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection)), NotifyPropertyChangedFor(nameof(HomeToggleLabel)), NotifyPropertyChangedFor(nameof(HomeToggleGlyph)), NotifyPropertyChangedFor(nameof(HomeHasSelection))] GameTile? _selectedGame;
    partial void OnSelectedGameChanged(GameTile? value) => RemoveArmed = false;
    public bool HasSelection => SelectedGame is not null;
    [ObservableProperty] string? _toast;
    [ObservableProperty] string _hostDisplayName = "";
    [ObservableProperty] bool _hostIsTv;
    [ObservableProperty] IReadOnlyList<PadState> _pads = [];
    public bool HasPads => Pads.Count > 0;
    partial void OnPadsChanged(IReadOnlyList<PadState> value) => OnPropertyChanged(nameof(HasPads));
    [ObservableProperty] bool _remapRequired;
    [ObservableProperty] DisplayItem? _inspectedDisplay;
    [ObservableProperty] string _preferredStatus = "";
    [ObservableProperty] string _fallbackText = "";
    [ObservableProperty] bool _isScanning;
    [ObservableProperty] bool _isFirstRun;

    public bool IsGameRunning => _launcher.Running is not null;
    public string RunningName => _launcher.Running?.Name ?? "";

    // Library filters
    [ObservableProperty] string _filter = "All";
    [ObservableProperty] string _sort = "Name";
    [ObservableProperty] string _search = "";
    partial void OnFilterChanged(string value) => ApplyLibraryView();
    partial void OnSortChanged(string value) => ApplyLibraryView();
    partial void OnSearchChanged(string value) => ApplyLibraryView();

    // Test Display overlay
    [ObservableProperty] DisplayItem? _testing;
    [ObservableProperty] int _testCountdown;

    // Display chooser overlay (per-game fallback "Ask")
    [ObservableProperty] GameTile? _choosingFor;

    // Add / Edit overlay
    [ObservableProperty] bool _isEditing;
    long _editId;
    [ObservableProperty] string _editTitle = "";
    [ObservableProperty] string _editImportDescription = "";
    [ObservableProperty] bool _editIsManual = true;
    [ObservableProperty] string _newName = "";
    [ObservableProperty] string _newExe = "";
    [ObservableProperty] string _newArgs = "";
    [ObservableProperty] string _newCustomExe = "";
    [ObservableProperty] string _defaultLaunchText = "";
    [ObservableProperty] bool _removeArmed;
    [ObservableProperty] bool _editingExisting;
    /// <summary>Game being started: shows the "Starting…" screen until its window appears.</summary>
    [ObservableProperty] GameTile? _launching;
    [RelayCommand] void DismissLaunch() => Launching = null;
    [ObservableProperty] string _newWorkDir = "";
    [ObservableProperty] string _newCategories = "";
    [ObservableProperty] string _newCover = "";
    [ObservableProperty] string _newBackground = "";
    [ObservableProperty] bool _newAutoPlace = true;
    [ObservableProperty] bool _newRunElevated;
    [ObservableProperty] bool _newAllowPause;
    [ObservableProperty] string _newHelperChoice = "Off";
    [ObservableProperty] bool _newShowOnHome = true;
    public ObservableCollection<TargetOption> TargetOptions { get; } = [];
    [ObservableProperty] TargetOption? _newTarget;
    public GameTargetFallback[] Fallbacks { get; } = [GameTargetFallback.FollowLauncher, GameTargetFallback.WindowsPrimary, GameTargetFallback.Ask];
    [ObservableProperty] GameTargetFallback _newFallback = GameTargetFallback.FollowLauncher;

    /// <summary>Kept for the window: true while the Add/Edit overlay is open.</summary>
    public bool IsAdding => IsEditing;
    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(IsAdding));

    // ---------------- Library data ----------------
    public IEnumerable<GameTile> RecentGames => _all.Where(g => g.LastPlayedUtc.HasValue)
        .OrderByDescending(g => g.LastPlayedUtc).ThenBy(g => g.Id)
        .Take(4).Select(g => new GameTile(g));

    void ReloadGames()
    {
        var sel = SelectedGame?.G.Id;
        _all = _repo.GetAll().Where(g => g.Installed && !g.Hidden).ToList();

        HomeGames.Clear();
        foreach (var g in HomeOrdered(_all.Where(g => g.ShowOnHome)).Take(HomeLimit))
            HomeGames.Add(new GameTile(g));
        OnPropertyChanged(nameof(HomeHasSelection)); OnPropertyChanged(nameof(HasHomeGames)); OnPropertyChanged(nameof(HomeUsageText));

        var filters = new[] { "All", "Favorites" }
            .Concat(_all.Select(g => GameTile.SourceName(g.Source)).Distinct().Order())
            .Concat(_all.SelectMany(g => g.Categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order().Select(c => "# " + c)).ToList();
        if (!filters.SequenceEqual(Filters)) { Filters.Clear(); foreach (var f in filters) Filters.Add(f); }
        if (!Filters.Contains(Filter)) Filter = "All";
        ApplyLibraryView();

        SelectedGame = HomeGames.FirstOrDefault(t => t.G.Id == sel) ?? LibraryGames.FirstOrDefault(t => t.G.Id == sel) ?? HomeGames.FirstOrDefault();
    }

    void ApplyLibraryView()
    {
        IEnumerable<Game> q = _all;
        if (Filter == "Favorites") q = q.Where(g => g.Favorite);
        else if (Filter.StartsWith("# ")) q = q.Where(g => g.Categories.Split(',').Any(c => c.Trim().Equals(Filter[2..], StringComparison.OrdinalIgnoreCase)));
        else if (Filter != "All") q = q.Where(g => GameTile.SourceName(g.Source) == Filter);
        if (!string.IsNullOrWhiteSpace(Search)) q = q.Where(g => g.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));
        q = Sort switch
        {
            "Recently played" => q.OrderByDescending(g => g.LastPlayedUtc ?? DateTime.MinValue),
            "Most played" => q.OrderByDescending(g => g.PlaytimeSeconds),
            "Recently added" => q.OrderByDescending(g => g.AddedUtc),
            _ => q.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        LibraryGames.Clear();
        foreach (var g in q) LibraryGames.Add(new GameTile(g));
    }

    public void RefreshDisplays()
    {
        var s = DisplaySettings;
        var snapshot = _watcher.Current;
        var preferred = DisplayMatcher.Match(s.Preferred, snapshot.Displays);
        var fallback = s.FallbackMode == FallbackMode.SpecificDisplay
            ? DisplayMatcher.Match(s.Fallback, snapshot.Displays).Display : snapshot.Primary;
        var host = _placement.VisibleHost;
        var rows = snapshot.Displays.Select(d => new DisplayItem(d, d == preferred.Display, d == fallback, d == host)).ToList();
        if (!Displays.SequenceEqual(rows))
        {
            var previous = InspectedDisplay?.Info;
            var inspected = DisplayMatcher.Match(previous?.Identity, snapshot.Displays).Display;
            Displays.Clear();
            foreach (var row in rows) Displays.Add(row);
            InspectedDisplay = rows.FirstOrDefault(d => d.Info == inspected)
                ?? rows.FirstOrDefault(d => d.IsHost) ?? rows.FirstOrDefault();
        }
        HostDisplayName = host?.Identity.FriendlyName ?? "Location unavailable";
        HostIsTv = host?.LooksLikeTv == true;
        RemapRequired = preferred.Kind == MatchKind.Ambiguous;
        PreferredStatus = s.Preferred is null ? "None selected" : preferred.Kind switch
        {
            MatchKind.Ambiguous => $"{s.Preferred.FriendlyName} · needs identification",
            MatchKind.NotFound => $"{s.Preferred.FriendlyName} · offline",
            _ when preferred.Display?.IsActive != true => $"{s.Preferred.FriendlyName} · connected, not extended",
            _ => s.Preferred.FriendlyName
        };
        FallbackText = s.FallbackMode == FallbackMode.WindowsPrimary ? "Fallback: Windows primary display" : $"Fallback: {s.Fallback?.FriendlyName}";
    }

    public void ShowToast(string msg) { Toast = msg; _toastTimer.Stop(); _toastTimer.Start(); }

    // ---------------- Navigation ----------------
    [RelayCommand] void ShowHome() => CurrentPage = Page.Home;
    [RelayCommand] void ShowLibrary() => CurrentPage = Page.Library;
    [RelayCommand] void ShowSettings() { RefreshDisplays(); RefreshAudio(); CurrentPage = Page.Settings; }

    /// <summary>L1/R1.</summary>
    public void CycleTab(int delta)
    {
        var next = (Page)(((int)CurrentPage + delta + 3) % 3);
        if (next == Page.Settings) { RefreshDisplays(); RefreshAudio(); }
        CurrentPage = next;
    }

    /// <summary>Triangle: favorite the selected game.</summary>
    public void ContextAction() { if (!IsSettings) ToggleFavorite(); }

    /// <summary>Back / Circle / Esc: closes the topmost layer.</summary>
    public void Back()
    {
        if (Launching is not null) Launching = null;
        else if (PowerMenuOpen) PowerMenuOpen = false;
        else if (Testing is not null) EndTest(false);
        else if (ChoosingFor is not null) ChoosingFor = null;
        else if (IsEditing) IsEditing = false;
        else if (IsFirstRun) return;
        else CurrentPage = Page.Home;
    }

    // ---------------- Games ----------------
    [RelayCommand]
    async Task Play(GameTile? game)
    {
        if ((game ?? SelectedGame) is not { } t) return;
        Launching = t;
        var r = await _launcher.LaunchAsync(t.G);
        if (!r.Started) Launching = null;
        if (r.NeedsDisplayChoice) { RefreshDisplays(); ChoosingFor = t; }
        ShowToast(r.Message);
    }

    [RelayCommand]
    async Task ChooseDisplay(DisplayItem d)
    {
        if (ChoosingFor is not { } t) return;
        ChoosingFor = null;
        if (!d.CanUse) { ShowToast("That display is not part of the Windows desktop"); return; }
        Launching = t;
        var r = await _launcher.LaunchAsync(t.G, d.Info);
        if (!r.Started) Launching = null;
        ShowToast(r.Message);
    }

    [RelayCommand]
    async Task MoveGame()
    {
        var active = _watcher.Current.Active.ToList();
        if (_launcher.Running is null || active.Count < 2) { ShowToast("Move Game needs a running game and two active displays"); return; }
        // Cycle to the next active display after the one the game was last placed on (or the launcher's).
        var current = _launcher.Running.LastUsedDisplay ?? _placement.HostDisplay?.Identity;
        int i = active.FindIndex(d => d.Identity.DevicePath == current?.DevicePath);
        var next = active[(i + 1) % active.Count];
        ShowToast(await _launcher.MoveRunningGameAsync(next));
        _launcher.Running.LastUsedDisplay = next.Identity;
    }

    [RelayCommand]
    void ToggleHome()
    {
        if (SelectedGame is not { } t) return;
        var id = t.G.Id;
        if (!t.G.ShowOnHome && OnHomeCount >= HomeLimit)
        {
            ShowToast($"Home is full ({HomeLimit} games). Remove one from Home, or raise the limit in Settings › General.");
            return;
        }
        t.G.ShowOnHome = !t.G.ShowOnHome;
        if (t.G.ShowOnHome) t.G.HomeOrder = _repo.GetAll().Where(g => g.ShowOnHome).Select(g => g.HomeOrder + 1).DefaultIfEmpty(0).Max();   // new Home games go last
        _repo.Save(t.G);
        ShowToast(t.G.ShowOnHome ? $"{t.Name} added to Home" : $"{t.Name} removed from Home");
        OnPropertyChanged(nameof(HomeToggleLabel)); OnPropertyChanged(nameof(HomeToggleGlyph));
        ReloadGames();
        SelectedGame = IsHome && !t.G.ShowOnHome ? HomeGames.FirstOrDefault()   // removed from Home: move to the next Home game
            : LibraryGames.Concat(HomeGames).FirstOrDefault(x => x.G.Id == id);
    }

    /// <summary>Home shows a game's title/Play only for games that are on Home (the selection is shared with Library).</summary>
    public bool HomeHasSelection => SelectedGame is { } s && HomeGames.Contains(s);
    public bool HasHomeGames => HomeGames.Count > 0;

    /// <summary>Home and Library share one selection; coming back to Home re-selects a Home game.</summary>
    partial void OnCurrentPageChanged(Page value)
    {
        if (value == Page.Home && !HomeHasSelection && HomeGames.Count > 0) SelectedGame = HomeGames[0];
    }



    /// <summary>"Extend desktop" on a connected display that isn't part of the desktop (Win+P › Extend; only on request).</summary>
    [RelayCommand]
    void ExtendDisplay(DisplayItem d)
    {
        if (d.CanUse) { ShowToast($"{d.Info.Identity.FriendlyName} is already part of the desktop"); return; }
        int rc = Infrastructure.Displays.DisplayTopology.Extend();
        Serilog.Log.Information("Extend to {Display} requested: SetDisplayConfig rc={Rc}", d.Info.Identity, rc);
        ShowToast(rc == 0 ? $"Extending the desktop to {d.Info.Identity.FriendlyName}…" : $"Windows couldn't extend the desktop (error {rc}). Try Win+P › Extend.");
        if (rc == 0 && d.IsPreferred) _placement.GoToPreferredSoon(restoreFocus: QuickMenuWindow.Current?.OverGame != true);
        _watcher.Schedule("extend requested");
    }
    /// <summary>Makes a display the Windows main display (taskbar, new windows) — only when the user presses the button.
    /// The arrangement, resolution, refresh rate and HDR stay as they are.</summary>
    [RelayCommand]
    void MakeMainDisplay(DisplayItem d)
    {
        if (!d.CanMakeMain || d.Info.GdiName is not { } gdi) { ShowToast($"{d.Info.Identity.FriendlyName} is already the main display"); return; }
        var err = Infrastructure.Displays.DisplayTopology.SetMain(gdi);
        Serilog.Log.Information("Make {Display} the main display: {Result}", d.Info.Identity, err ?? "ok");
        ShowToast(err is null ? $"{d.Info.Identity.FriendlyName} is now the main display" : $"Couldn't make {d.Info.Identity.FriendlyName} the main display: {err}");
        _watcher.Schedule("main display changed");
    }

    /// <summary>PS triple press (when enabled): extend the Windows desktop to the preferred display if it is
    /// connected but not part of the desktop. The display watcher then moves the launcher there as usual.</summary>
    public void ExtendToPreferredDisplay(bool fromGame)
    {
        if (!ControllerSettings.PsDoubleExtendsDisplay) { Serilog.Log.Information("PS triple press ignored: setting off"); return; }
        if (DisplaySettings.Preferred is not { } pref) { ShowToast("Choose a preferred display in Settings first"); return; }
        var m = DisplayMatcher.Match(pref, _watcher.Current.Displays).Display;
        Serilog.Log.Information("PS triple press: preferred {Pref} → {Match}", pref, m?.Status.ToString() ?? "not connected");
        if (m is null) { ShowToast($"{pref.FriendlyName} isn't connected (is it on, and on the right input?)"); return; }
        // Already part of the desktop: nothing to extend. The second press just closed the quick menu, as a normal press would.
        if (m.IsActive) return;
        int rc = Infrastructure.Displays.DisplayTopology.Extend();
        if (rc == 0) _placement.GoToPreferredSoon(restoreFocus: !fromGame);
        Serilog.Log.Information("Extend requested: SetDisplayConfig rc={Rc}", rc);
        ShowToast(rc == 0 ? $"Extending the desktop to {pref.FriendlyName}…" : $"Windows couldn't extend the desktop (error {rc}). Try Win+P › Extend.");
        _watcher.Schedule("extended to preferred display");
    }

    // ---------------- Power menu ----------------
    [ObservableProperty] bool _powerMenuOpen;
    [ObservableProperty] string? _powerArmed;   // restart/shutdown need a second press
    [RelayCommand] void OpenPower() { PowerArmed = null; PowerMenuOpen = true; }
    partial void OnPowerMenuOpenChanged(bool value) => PowerArmed = null;
    public string RestartLabel => PowerArmed == "restart" ? "Press again to restart" : "Restart";
    public string ShutdownLabel => PowerArmed == "shutdown" ? "Press again to shut down" : "Shut down";
    partial void OnPowerArmedChanged(string? value) { OnPropertyChanged(nameof(RestartLabel)); OnPropertyChanged(nameof(ShutdownLabel)); }

    [RelayCommand]
    void Power(string kind)
    {
        if (kind is "restart" or "shutdown" && PowerArmed != kind)
        {
            PowerArmed = kind;
            if (IsGameRunning) ShowToast($"{RunningName} is still running — save your game first");
            return;
        }
        PowerMenuOpen = false;
        _system.Power(kind);
    }

    /// <summary>Send Gaming Hub to the taskbar without closing it (PS or the taskbar brings it back).</summary>
    public event Action? MinimizeRequested;
    [RelayCommand] void Minimize() { PowerMenuOpen = false; MinimizeRequested?.Invoke(); }

    // ---------------- Settings categories ----------------
    public SettingsCategory[] SettingsCategories { get; } =
    [
        new("Displays", "\uE7F4"), new("Sound", "\uE767"), new("Controller", "\uE7FC"), new("Home", "\uE80F"),
        new("Library", "\uE8F1"), new("System", "\uE713"), new("About", "\uE946"),
    ];
    [ObservableProperty] SettingsCategory? _settingsSection;
    public string DataDir => App.DataDir;

    /// <summary>L1/R1 inside Settings: previous/next category; past the ends, the normal tab switch.</summary>
    public bool StepSettingsCategory(int delta)
    {
        int i = Array.IndexOf(SettingsCategories, SettingsSection ?? SettingsCategories[0]) + delta;
        if (i < 0 || i >= SettingsCategories.Length) return false;
        SettingsSection = SettingsCategories[i];
        return true;
    }

    // ---------------- Home order ----------------
    IEnumerable<Game> HomeOrdered(IEnumerable<Game> games) => _settings.Current.HomeCustomOrder
        ? games.OrderBy(g => g.HomeOrder).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
        : games.OrderByDescending(g => g.LastPlayedUtc ?? DateTime.MinValue).ThenByDescending(g => g.Favorite).ThenByDescending(g => g.AddedUtc);

    public static string[] HomeOrders { get; } = ["Most recently played", "My own order"];
    public string HomeOrderMode
    {
        get => HomeOrders[_settings.Current.HomeCustomOrder ? 1 : 0];
        set { SetCustomOrder(value == HomeOrders[1]); OnPropertyChanged(); }
    }

    void SetCustomOrder(bool custom)
    {
        if (custom && !_settings.Current.HomeCustomOrder)
        {
            int i = 0;   // start from what Home shows now
            foreach (var g in HomeOrdered(_repo.GetAll().Where(g => g.ShowOnHome)).ToList()) { g.HomeOrder = i++; _repo.Save(g); }
        }
        _settings.Current.HomeCustomOrder = custom;
        SaveSettings();
        ReloadGames();
        OnPropertyChanged(nameof(HomeOrderMode));
    }

    /// <summary>Moves the selected Home game one place left/right (switches Home to "My own order").</summary>
    public void MoveHomeGame(int delta)
    {
        if (SelectedGame is not { } t || !HomeGames.Contains(t)) return;
        SetCustomOrder(true);
        var list = HomeOrdered(_repo.GetAll().Where(g => g.ShowOnHome)).ToList();
        int i = list.FindIndex(g => g.Id == t.G.Id), j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        for (int k = 0; k < list.Count; k++) if (list[k].HomeOrder != k) { list[k].HomeOrder = k; _repo.Save(list[k]); }
        ReloadGames();
        SelectedGame = HomeGames.FirstOrDefault(x => x.G.Id == t.G.Id);
    }
    // ---------------- Home size ----------------
    public static int[] HomeLimits { get; } = [8, 12, 16, 24, 32, 48];
    public int HomeLimit
    {
        get => _settings.Current.HomeMaxGames;
        set { _settings.Current.HomeMaxGames = value; SaveSettings(); ReloadGames(); OnPropertyChanged(); }
    }
    int OnHomeCount => _repo.GetAll().Count(g => g.ShowOnHome && g.Installed && !g.Hidden);
    public string HomeUsageText => $"{Math.Min(OnHomeCount, HomeLimit)} of {HomeLimit} spots used."
        + (OnHomeCount > HomeLimit ? $" {OnHomeCount - HomeLimit} more are added to Home; the least recently played are hidden." : "");


    /// <summary>Pin to add to Home, unpin to remove (Segoe Fluent Icons).</summary>
    public string HomeToggleGlyph => SelectedGame?.G.ShowOnHome == true ? "" : "";

    public string HomeToggleLabel => SelectedGame?.G.ShowOnHome == true ? "Remove from Home" : "Add to Home";

    [RelayCommand]
    void ToggleFavorite()
    {
        if (SelectedGame is not { } t) return;
        t.G.Favorite = !t.G.Favorite;
        _repo.Save(t.G);
        ReloadGames();
        ShowToast(t.G.Favorite ? $"★ {t.Name} added to favorites" : $"{t.Name} removed from favorites");
    }

    [RelayCommand]
    void RemoveGame()
    {
        if (SelectedGame is not { } t) return;
        if (!RemoveArmed) { RemoveArmed = true; ShowToast($"Press Remove again to remove {t.Name} from the library"); return; }
        RemoveArmed = false;
        var g = t.G;
        if (g.RunElevated) SyncElevatedTask(g, false);   // don't leave an admin task behind
        // Discovered games would return on rescan, so hide them; manual entries are deleted.
        if (g.Source == "manual") _repo.Delete(g.Id);
        else { g.Hidden = true; _repo.Save(g); }
        IsEditing = false;
        ReloadGames();
        ShowToast(g.Source == "manual" ? $"Removed {g.Name}" : $"Removed {g.Name} from the library (Settings › Show hidden games brings it back)");
    }

    [RelayCommand]
    void UnhideAll()
    {
        foreach (var g in _repo.GetAll().Where(g => g.Hidden)) { g.Hidden = false; _repo.Save(g); }
        ReloadGames();
        ShowToast("Hidden games restored");
    }

    void FillTargetOptions(Game? g)
    {
        TargetOptions.Clear();
        TargetOptions.Add(new("Follow Gaming Hub display", GameTargetMode.FollowLauncher, null));
        TargetOptions.Add(new("Windows primary display", GameTargetMode.WindowsPrimary, null));
        TargetOptions.Add(new("Last successfully used display", GameTargetMode.LastUsed, null));
        foreach (var d in _watcher.Current.Displays)
            TargetOptions.Add(new($"Always: {d.Identity.FriendlyName}", GameTargetMode.SpecificDisplay, d.Identity));
        if (g?.TargetMode == GameTargetMode.SpecificDisplay && g.TargetDisplay is { } td &&
            !TargetOptions.Any(o => o.Display is not null && DisplayMatcher.Match(td, [Fake(o.Display)]).Display is not null))
            TargetOptions.Add(new($"Always: {td.FriendlyName} (not connected)", GameTargetMode.SpecificDisplay, td));
        NewTarget = g is null ? TargetOptions[0]
            : g.TargetMode == GameTargetMode.SpecificDisplay
                ? TargetOptions.FirstOrDefault(o => o.Display is not null && g.TargetDisplay is not null && DisplayMatcher.Match(g.TargetDisplay, [Fake(o.Display)]).Display is not null) ?? TargetOptions[0]
                : TargetOptions.First(o => o.Mode == g.TargetMode && o.Display is null);
    }

    static DisplayInfo Fake(DisplayIdentity id) => new() { Identity = id };

    [RelayCommand] void BeginAdd() => BeginAdd(null);

    public void BeginAdd(string? exe)
    {
        _editId = 0;
        EditTitle = "Add Game";
        EditIsManual = true;
        NewExe = exe ?? ""; NewArgs = ""; NewWorkDir = ""; NewCategories = ""; NewCover = ""; NewBackground = "";
        NewAutoPlace = true; NewRunElevated = false; NewAllowPause = false; NewHelperChoice = "Off"; NewShowOnHome = true; NewCustomExe = ""; RemoveArmed = false; EditingExisting = false; NewFallback = GameTargetFallback.FollowLauncher;
        NewName = exe is null ? "" : ExeDisplayName(exe);
        FillTargetOptions(null);
        IsEditing = true;
    }

    [RelayCommand]
    void BeginEdit()
    {
        if (SelectedGame is not { } t) return;
        var g = t.G;
        _editId = g.Id;
        EditTitle = $"Edit {g.Name}";
        EditImportDescription = g.Source switch
        {
            "manual" => "Added manually",
            "shortcut" => "Imported automatically from a Games folder shortcut",
            "steam" or "epic" or "gog" or "ubisoft" => $"Imported automatically from {t.SourceText}",
            _ => "Import method unknown"
        };
        EditIsManual = g.Source == "manual";
        NewName = g.Name; NewExe = g.ExecutablePath; NewArgs = g.Arguments; NewWorkDir = g.WorkingDirectory ?? "";
        NewCategories = g.Categories; NewCover = g.CoverPath ?? ""; NewBackground = g.BackgroundPath ?? "";
        NewAutoPlace = g.AutoPlace; NewRunElevated = g.RunElevated; NewAllowPause = g.AllowPause; NewHelperChoice = g.UseControllerHelper ? (ControllerHelperService.ByName(g.HelperName)?.Name ?? "Off") : "Off"; NewShowOnHome = g.ShowOnHome; NewCustomExe = g.CustomExe ?? ""; RemoveArmed = false; EditingExisting = true;
        DefaultLaunchText = g.LaunchUri is { } u ? $"Default: through {GameTile.SourceName(g.Source)} ({u.Split('?')[0]})" : $"Default: {g.ExecutablePath}"; NewFallback = g.TargetFallback;
        FillTargetOptions(g);
        IsEditing = true;
    }

    static string ExeDisplayName(string exe)
    {
        try
        {
            var n = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim();
            if (!string.IsNullOrEmpty(n)) return n;
        }
        catch { /* metadata optional */ }
        return Path.GetFileNameWithoutExtension(exe);
    }

    [RelayCommand]
    void BrowseExe()
    {
        var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "Choose game executable" };
        if (dlg.ShowDialog() != true) return;
        NewExe = dlg.FileName;
        if (string.IsNullOrWhiteSpace(NewName)) NewName = ExeDisplayName(dlg.FileName);
    }

    [RelayCommand]
    void BrowseImage(string which)
    {
        var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.bmp", Title = which == "cover" ? "Choose cover art" : "Choose background art" };
        if (dlg.ShowDialog() != true) return;
        if (which == "cover") NewCover = dlg.FileName; else NewBackground = dlg.FileName;
    }

    [RelayCommand]
    void SaveGame()
    {
        if (EditIsManual && !File.Exists(NewExe.Trim('"'))) { ShowToast("Choose an existing .exe file"); return; }
        var g = _editId == 0 ? new Game() : _repo.GetAll().First(x => x.Id == _editId);
        g.Name = string.IsNullOrWhiteSpace(NewName) ? ExeDisplayName(NewExe) : NewName.Trim();
        if (EditIsManual) g.ExecutablePath = NewExe.Trim('"');
        g.Arguments = NewArgs;
        g.WorkingDirectory = string.IsNullOrWhiteSpace(NewWorkDir) ? null : NewWorkDir;
        g.Categories = NewCategories;
        g.CoverPath = string.IsNullOrWhiteSpace(NewCover) ? null : NewCover;
        g.BackgroundPath = string.IsNullOrWhiteSpace(NewBackground) ? null : NewBackground;
        g.AutoPlace = NewAutoPlace; g.AllowPause = NewAllowPause; g.UseControllerHelper = NewHelperChoice != "Off"; g.HelperName = g.UseControllerHelper ? NewHelperChoice : null; g.ShowOnHome = NewShowOnHome;
        if (!EditIsManual) g.CustomExe = string.IsNullOrWhiteSpace(NewCustomExe) ? null : NewCustomExe.Trim().Trim('"');
        g.TargetFallback = NewFallback;
        g.TargetMode = NewTarget?.Mode ?? GameTargetMode.FollowLauncher;
        g.TargetDisplay = NewTarget?.Display;
        _repo.Save(g);   // first save gives a new game its id (the task is named after it)
        var elevNote = SyncElevatedTask(g, NewRunElevated);
        _repo.Save(g);
        IsEditing = false;
        ReloadGames();
        SelectedGame = HomeGames.Concat(LibraryGames).FirstOrDefault(x => x.G.Id == g.Id);
        ShowToast(elevNote ?? $"Saved {g.Name}");
    }

    /// <summary>Creates, updates or removes the game's "run as administrator" task. Windows asks for approval once
    /// (mouse/keyboard: the prompt is on the secure desktop). Returns a message when something changed or failed.</summary>
    string? SyncElevatedTask(Game g, bool want)
    {
        if (!want)
        {
            if (!g.RunElevated && ElevatedTasks.Query(ElevatedGames.TaskName(g)) is null) return null;
            var err = ElevatedGames.Delete(g);
            g.RunElevated = false;
            Serilog.Log.Information("Admin task for {Game} removed: {Result}", g.Name, err ?? "ok");
            return err is null ? $"Saved {g.Name} — it starts normally again" : $"Saved {g.Name}, but its administrator task wasn't removed ({err})";
        }
        if (ElevatedGames.IsReady(g)) { g.RunElevated = true; return null; }
        ShowToast("Approve the Windows prompt with the mouse, touchpad or Alt+Y — it's needed only once");
        var create = ElevatedGames.Create(g);
        g.RunElevated = create is null;
        Serilog.Log.Information("Admin task for {Game}: {Result}", g.Name, create ?? "created");
        return create is null ? $"{g.Name} now starts as administrator without a prompt" : $"{g.Name} wasn't set up to run as administrator: {create}";
    }

    [RelayCommand] void CancelEdit() => IsEditing = false;

    public string RemoveLabel => RemoveArmed ? "Press again to remove" : "Remove from library";
    partial void OnRemoveArmedChanged(bool value) => OnPropertyChanged(nameof(RemoveLabel));

    [RelayCommand]
    void BrowseCustomExe()
    {
        var dlg = new OpenFileDialog { Filter = "Programs (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd", Title = "Choose what to launch for this game" };
        if (dlg.ShowDialog() == true) NewCustomExe = dlg.FileName;
    }


    // ---------------- Sound output (Windows default playback device) ----------------
    public ObservableCollection<AudioOutput> AudioOutputs { get; } = [];
    [ObservableProperty] AudioOutput? _selectedAudioOutput;
    bool _refreshingAudio;

    [RelayCommand]
    void RefreshAudio()
    {
        _refreshingAudio = true;
        try
        {
            AudioOutputs.Clear();
            foreach (var d in Infrastructure.Audio.AudioOutputs.List()) AudioOutputs.Add(d);
            SelectedAudioOutput = AudioOutputs.FirstOrDefault(d => d.IsDefault);
        }
        catch (Exception ex) { ShowToast($"Couldn't list sound devices: {ex.Message}"); }
        finally { _refreshingAudio = false; }
    }

    partial void OnSelectedAudioOutputChanged(AudioOutput? value)
    {
        if (_refreshingAudio || value is null || value.IsDefault) return;
        try
        {
            Infrastructure.Audio.AudioOutputs.SetDefault(value.Id);
            ShowToast($"Sound now plays on {value.Name}");
        }
        catch (Exception ex) { ShowToast($"Couldn't switch sound output: {ex.Message}"); }
        Dispatcher.CurrentDispatcher.BeginInvoke(RefreshAudio);   // re-read so IsDefault flags stay true to Windows
    }

    // ---------------- Game shortcut folders (e.g. Desktop\Games) ----------------
    public ObservableCollection<string> GameFolders { get; } = [];
    readonly List<FileSystemWatcher> _folderWatchers = [];
    readonly DispatcherTimer _folderDebounce = new() { Interval = TimeSpan.FromSeconds(3) };

    /// <summary>Watches the shortcut folders: adding/removing/renaming a shortcut rescans the library a few seconds later.</summary>
    void WatchGameFolders()
    {
        foreach (var w in _folderWatchers) w.Dispose();
        _folderWatchers.Clear();
        GameFolders.Clear();
        foreach (var f in _settings.Current.EffectiveGameFolders)
        {
            GameFolders.Add(f);
            if (!Directory.Exists(f)) continue;
            var w = new FileSystemWatcher(f) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            FileSystemEventHandler h = (_, _) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() => { _folderDebounce.Stop(); _folderDebounce.Start(); });
            w.Created += h; w.Deleted += h; w.Changed += h; w.Renamed += (s, e) => h(s, e);
            w.EnableRaisingEvents = true;
            _folderWatchers.Add(w);
        }
    }

    [RelayCommand]
    async Task AddGameFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder with game shortcuts" };
        if (dlg.ShowDialog() != true) return;
        var list = _settings.Current.EffectiveGameFolders.ToList();
        if (!list.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase)) list.Add(dlg.FolderName);
        _settings.Current.GameFolders = list;
        SaveSettings();
        WatchGameFolders();
        await RescanLibrary();
    }

    [RelayCommand]
    async Task RemoveGameFolder(string folder)
    {
        _settings.Current.GameFolders = _settings.Current.EffectiveGameFolders.Where(f => !f.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
        SaveSettings();
        WatchGameFolders();
        await RescanLibrary();
    }
    // ---------------- Controller helper (ZerkSense5 / DS4Windows) ----------------
    public HelperKind[] HelperKinds => HelperKind.All;
    HelperKind _setupHelper = HelperKind.Ds4Windows;
    public HelperKind SetupHelper
    {
        get => _setupHelper;
        set { if (value is null || value == _setupHelper) return; _setupHelper = value; _helperNoPrompt = null; OnPropertyChanged(); OnPropertyChanged(nameof(HelperStatus)); OnPropertyChanged(nameof(HelperNoPrompt)); OnPropertyChanged(nameof(HelperNoPromptLabel)); }
    }

    bool? _helperNoPrompt;   // cached: checking asks Task Scheduler
    /// <summary>Start the selected helper through its admin task, so its UAC prompt doesn't appear (set up once with approval).</summary>
    public bool HelperNoPrompt
    {
        get => _helperNoPrompt ??= _helper.NoPromptReady(SetupHelper);
        set
        {
            if (value == HelperNoPrompt) return;
            ShowToast("Approve the Windows prompt with the mouse or keyboard (Alt+Y) — it's needed only once");
            var err = _helper.SetNoPrompt(SetupHelper, value);
            _helperNoPrompt = _helper.NoPromptReady(SetupHelper);
            OnPropertyChanged();
            ShowToast(err is not null ? $"{SetupHelper.Name}: {err}"
                : value ? $"{SetupHelper.Name} now starts without the administrator prompt" : $"{SetupHelper.Name} starts normally again");
        }
    }
    public LightBarColor[] LightBarColors { get; } = [LightBarColor.Original, .. Enum.GetValues<LightBarColor>().Where(color => color != LightBarColor.Original)];
    public LightBarColor SelectedLightBar
    {
        get => ControllerSettings.ControllerOneLightBar;
        set
        {
            if (ControllerSettings.ControllerOneLightBar == value) return;
            ControllerSettings.ControllerOneLightBar = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public LightBarColor SelectedPlayerTwoLightBar
    {
        get => ControllerSettings.PlayerTwoLightBar;
        set
        {
            if (ControllerSettings.PlayerTwoLightBar == value) return;
            ControllerSettings.PlayerTwoLightBar = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public string[] TouchpadSpeeds { get; } = ["Slow", "Medium", "Normal", "Fast", "Very fast"];
    public string TouchpadSpeedName
    {
        get => TouchpadSpeeds[Math.Clamp(ControllerSettings.TouchpadSpeed, 1, 5) - 1];
        set { ControllerSettings.TouchpadSpeed = Array.IndexOf(TouchpadSpeeds, value) + 1; SaveSettings(); OnPropertyChanged(); }
    }

    public string FakerInputStatus => Infrastructure.Input.FakerInputMouse.IsInstalled()
        ? "FakerInput is installed: the touchpad also works on administrator (UAC) prompts and administrator windows."
        : "FakerInput isn't installed. Without it, the touchpad can't click administrator (UAC) prompts. Install it from its official page, then restart Gaming Hub.";

    [RelayCommand]
    void GetFakerInput() => Process.Start(new ProcessStartInfo("https://github.com/Ryochan7/FakerInput/releases/latest") { UseShellExecute = true });

    public string HelperNoPromptLabel =>$"Start {SetupHelper.Name} without the administrator prompt";

    public string HelperStatus => _helper.FindExe(SetupHelper) is { } p
        ? $"{SetupHelper.Purpose}. {(ControllerHelperService.IsRunning(SetupHelper) ? "Running" : "Installed")} at {p}. {HelperAfterGame}"
        : $"{SetupHelper.Name} isn't installed. Install it, or use Locate to select it.";

    string HelperAfterGame => SetupHelper.ShutdownArgs is null ? "It keeps running in the notification area after the game." : "It closes after the game.";

    /// <summary>Per-game choices in Game settings.</summary>
    public string[] HelperChoices { get; } = ["Off", .. HelperKind.All.Select(h => h.Name)];

    /// <summary>Status of the helper picked for the game being edited.</summary>
    public string GameHelperStatus => ControllerHelperService.ByName(NewHelperChoice) is not { } h
        ? "This game doesn't use a controller helper."
        : _helper.FindExe(h) is { } p
            ? $"{h.Purpose}. {(ControllerHelperService.IsRunning(h) ? "Running" : "Installed")} at {p}. {(h.ShutdownArgs is null ? "It keeps running in the notification area after the game." : "It closes after the game.")}"
            : $"{h.Name} isn't installed. Install it, or set its location in Settings › Controller.";

    partial void OnNewHelperChoiceChanged(string value) => OnPropertyChanged(nameof(GameHelperStatus));

    [RelayCommand]
    void BrowseHelper()
    {
        var h = SetupHelper;
        var dlg = new OpenFileDialog { Filter = $"{h.ExeName}|{h.ExeName}|Programs (*.exe)|*.exe", Title = $"Locate {h.ExeName}" };
        if (dlg.ShowDialog() != true) return;
        if (!_helper.SetPath(h, dlg.FileName)) { ShowToast($"Select the executable for {h.Name}."); return; }
        _helperNoPrompt = null;   // a different exe no longer matches its task
        OnPropertyChanged(nameof(HelperStatus));
        OnPropertyChanged(nameof(HelperNoPrompt));
    }

    // ---------------- Settings ----------------
    [RelayCommand]
    void SaveSettings()
    {
        _settings.Save();
        _system.ApplyStartup(AppSettings.StartWithWindows || AppSettings.ConsoleMode);
        _placement.Evaluate("settings");
    }

    [RelayCommand]
    void SetPreferred(DisplayItem d)
    {
        DisplaySettings.Preferred = d.Info.Identity;
        SaveSettings();
        ShowToast($"Preferred display: {d.Info.Identity.FriendlyName}");
    }

    [RelayCommand]
    void SetFallback(DisplayItem d)
    {
        DisplaySettings.FallbackMode = FallbackMode.SpecificDisplay;
        DisplaySettings.Fallback = d.Info.Identity;
        SaveSettings();
    }

    [RelayCommand]
    void UsePrimaryFallback()
    {
        DisplaySettings.FallbackMode = FallbackMode.WindowsPrimary;
        SaveSettings();
    }

    Task _manualScan = Task.CompletedTask;

    // WPF drops keyboard focus when an async command disables its button.
    // Keep the button enabled and let repeated presses await the same scan.
    [RelayCommand(AllowConcurrentExecutions = true)]
    public Task RescanLibrary() =>
        _manualScan.IsCompleted ? _manualScan = Rescan(quiet: false) : _manualScan;

    /// <summary>Quiet scans (startup, folder changes) only speak up when the library actually changed.</summary>
    public async Task Rescan(bool quiet)
    {
        if (IsScanning) return;
        IsScanning = true;
        if (!quiet) ShowToast("Scanning for installed games…");
        try
        {
            var summary = await _library.ScanAsync();
            if (!quiet || summary.Changed) ShowToast(summary.Message);
        }
        finally { IsScanning = false; ReloadGames(); }
        await FillArtworkAsync();
    }

    [RelayCommand] void RescanDisplays() => _watcher.Schedule("manual rescan");
    [RelayCommand] void OpenLogs() => _system.OpenDataFolder();
    [RelayCommand]
    void OpenSteamControllerSettings()
    {
        try { Process.Start(new ProcessStartInfo("steam://settings/controller") { UseShellExecute = true }); }
        catch (Exception ex) { ShowToast($"Couldn't open Steam: {ex.Message}"); }
    }

    [RelayCommand] void OpenNotificationSettings() => Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
    [RelayCommand] void ExportDiagnostics() => ShowToast(_system.ExportDiagnostics());
    [RelayCommand] void Backup() => ShowToast(_system.Backup());
    [RelayCommand] void Restore() { if (_system.Restore() is { } msg) ShowToast(msg); }
    [RelayCommand] void Exit() => System.Windows.Application.Current.Shutdown();

    [RelayCommand]
    void FinishFirstRun()
    {
        AppSettings.FirstRunCompleted = true;
        _settings.Save();
        IsFirstRun = false;
    }

    // ---------------- Test Display ----------------
    [RelayCommand]
    void TestDisplay(DisplayItem d)
    {
        if (!d.CanUse) { ShowToast("That display is not part of the Windows desktop"); return; }
        Testing = d;
        TestCountdown = TestSeconds;
        _placement.BeginTest(d.Info);
        _testTimer.Start();
    }

    [RelayCommand] void ConfirmTest() => EndTest(true);
    [RelayCommand] void RevertTest() => EndTest(false);

    void EndTest(bool confirm)
    {
        _testTimer.Stop();
        if (Testing is null) return;
        var name = Testing.Info.Identity.FriendlyName;
        Testing = null;
        _placement.EndTest(confirm);
        RefreshDisplays();
        ShowToast(confirm ? $"{name} is now the preferred display" : "Display test reverted");
    }
}

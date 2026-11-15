using System.Diagnostics;
using GamingHub.Core.Displays;
using GamingHub.Core.Games;
using GamingHub.Infrastructure.Displays;
using GamingHub.Infrastructure.Games;
using Microsoft.Extensions.Logging;

namespace GamingHub.App.Services;

public sealed record LaunchOutcome(bool Started, bool NeedsDisplayChoice, string Message);

/// <summary>
/// Launches a game, finds its real window, places it on the resolved display (when compatible),
/// verifies placement by window bounds, and restores the launcher when the game exits.
/// Never kills or moves a running game in response to display changes.
/// </summary>
public sealed class GameLaunchService(IGameRepository repo, DisplayWatcher watcher, LauncherPlacementService placement,
    ControllerHelperService helper, ILogger<GameLaunchService> log)
{
    const int FindWindowTimeoutS = 120, MaxPlacements = 3;

    public Game? Running { get; private set; }
    public DateTime? DetectedSessionStartUtc { get; private set; }
    IntPtr _window;
    public event Action<Game>? Exited;          // UI thread
    public event Action<string>? Notify;        // UI thread
    public event Action? SessionChanged;        // UI thread
    /// <summary>The game's window appeared, or the search for it ended (UI thread).</summary>
    public event Action? LaunchSettled;

    static void Ui(Action a) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(a);

    public async Task<LaunchOutcome> LaunchAsync(Game g, DisplayInfo? chosen = null)
    {
        if (Running is not null) return new(false, false, $"{Running.Name} is already running");
        if (g.RunElevated && !ElevatedGames.IsReady(g))   // older or moved setup: refresh it (one Windows approval)
        {
            Notify?.Invoke($"Updating {g.Name}'s administrator setup — approve the Windows prompt once (touchpad, mouse or Alt+Y)");
            if (await Task.Run(() => ElevatedGames.Create(g)) is { } err) return new(false, false, $"{g.Name}'s administrator setup wasn't updated: {err}");
        }
        if (!g.UseControllerHelper) return Launch(g, chosen);
        Running = g;   // blocks a second Play while the controller helper starts
        Process? ownedHelper;
        try
        {
            var kind = ControllerHelperService.ByName(g.HelperName) ?? throw new InvalidOperationException("Choose a controller helper in Game settings.");
            Notify?.Invoke($"Starting {kind.Name}…");
            ownedHelper = await helper.EnsureRunningAsync(kind);
        }
        catch (Exception ex) { Running = null; return new(false, false, ex.Message); }
        finally { if (Running == g) Running = null; }
        var r = Launch(g, chosen, ownedHelper);
        if (!r.Started) helper.StopIfStartedByUs(ownedHelper);
        return r;
    }

    LaunchOutcome Launch(Game g, DisplayInfo? chosen, Process? ownedHelper = null)
    {
        if (Running is not null) return new(false, false, $"{Running.Name} is already running");
        var target = chosen is not null ? new GameTargetResult(chosen, false, "chosen by user")
            : GameTargetResolver.Resolve(g, watcher.Current, placement.HostDisplay);
        if (target.AskUser) return new(false, true, "Choose a display for this game");

        LaunchCommand cmd;
        try { cmd = LaunchCommandBuilder.Build(g); }
        catch (InvalidOperationException ex) { return new(false, false, ex.Message); }
        bool isUri = cmd.FileName.Contains("://");
        if (!isUri && !File.Exists(cmd.FileName)) return new(false, false, $"Executable not found: {cmd.FileName}");

        Process? proc = null;
        if (g.RunElevated && !isUri)   // through its Gaming Hub task: admin rights, no UAC prompt
        {
            if (!ElevatedGames.IsReady(g))
                return new(false, false, $"{g.Name}'s administrator setup is missing or out of date. Open its game settings and save to set it up again.");
            if (ElevatedTasks.Start(ElevatedGames.TaskName(g)) is { } err)
            {
                log.LogError("Elevated launch of {Game} failed: {Err}", g.Name, err);
                return new(false, false, $"Could not start {g.Name} as administrator: {err}");
            }
        }
        else try { proc = Process.Start(new ProcessStartInfo(cmd.FileName, cmd.Arguments) { WorkingDirectory = cmd.WorkingDirectory, UseShellExecute = true }); }
        catch (Exception ex)
        {
            log.LogError(ex, "Launch failed for {Game}", g.Name);
            return new(false, false, $"Could not start {g.Name}: {ex.Message}");
        }
        log.LogInformation("Launched {Game} via {Cmd} pid={Pid}; target={Target} ({Reason})",
            g.Name, isUri ? cmd.FileName : g.RunElevated ? "admin task" : "exe", proc?.Id, target.Display?.Identity.FriendlyName, target.Reason);

        Running = g;
        SessionChanged?.Invoke();
        var dir = _dir = TrackableDir(g.InstallDir ?? (isUri ? null : Path.GetDirectoryName(cmd.FileName)));
        // Platform URIs start a short-lived handler, not the game: track only the install folder for those.
        if (isUri) proc?.Dispose();
        _ = Task.Run(() => TrackAsync(g, dir, isUri ? null : proc, target.Display, ownedHelper));
        return new(true, false, $"Starting {g.Name} on {target.Display?.Identity.FriendlyName ?? "its default display"}");
    }

    async Task TrackAsync(Game g, string? dir, Process? proc, DisplayInfo? target, Process? ownedHelper)
    {
        var started = DateTime.UtcNow;
        bool verified = false, seen = false;
        try
        {
            // 1) Find the real game window: same window seen 3 polls in a row (skips short splash screens).
            HashSet<int> pids = [];
            IntPtr candidate = IntPtr.Zero;
            int stable = 0, placements = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < FindWindowTimeoutS)
            {
                await Task.Delay(_window == IntPtr.Zero ? 1000 : 2000);
                pids = Pids(dir, proc);
                if (pids.Count > 0)
                {
                    if (!seen) Ui(() => { DetectedSessionStartUtc = started; SessionChanged?.Invoke(); });
                    seen = true;
                }
                else if (seen || (proc is not null && sw.Elapsed.TotalSeconds > 45)) break;   // exited; platform launches get the full timeout
                if (_window != IntPtr.Zero && sw.Elapsed.TotalSeconds > 30) break;   // window settled; switch to cheap exit polling
                var w = GameWindows.FindMainWindow(pids);
                stable = w != IntPtr.Zero && w == candidate ? stable + 1 : (w != IntPtr.Zero ? 1 : 0);
                candidate = w;
                if (stable < 3 || w == _window) continue;

                if (_window == IntPtr.Zero) Ui(() => LaunchSettled?.Invoke());   // first real window: hide "Starting…"
                _window = w;   // new main window (first one, or splash → game switch)
                log.LogInformation("{Game}: main window {Hwnd:X} found after {S:0}s ({Count} processes)", g.Name, (long)w, sw.Elapsed.TotalSeconds, pids.Count);
                if (target is not null && g.AutoPlace && placements++ < MaxPlacements)
                    verified = await PlaceAsync(g, w, target);
            }
            Ui(() => LaunchSettled?.Invoke());   // window search over (found or not)
            if (!seen)
                log.LogWarning("{Game}: game process not detected after {S:0}s (platform launcher may be updating or waiting for sign-in); not tracked", g.Name, sw.Elapsed.TotalSeconds);
            else if (_window == IntPtr.Zero)
                log.LogWarning("{Game}: no game window detected (placement skipped)", g.Name);

            // 2) Wait for exit cheaply: poll known PIDs, rescan the install folder only when they are all gone.
            int emptyPolls = 0;
            while (emptyPolls < 2)
            {
                await Task.Delay(3000);
                pids.RemoveWhere(id => { try { using var p = Process.GetProcessById(id); return p.HasExited; } catch { return true; } });
                if (pids.Count == 0) pids = Pids(dir, proc);
                if (pids.Count > 0) seen = true;
                emptyPolls = pids.Count == 0 ? emptyPolls + 1 : 0;
            }
        }
        catch (Exception ex)
        {
            ownedHelper?.Dispose(); // Exit could not be confirmed. Leave the helper running.
            ownedHelper = null;
            log.LogError(ex, "Tracking {Game} failed", g.Name);
        }
        finally { proc?.Dispose(); }

        if (!seen)
        {
            ownedHelper?.Dispose();
            // Never saw the game: no playtime, and don't pull the launcher forward later over a game that may still start.
            Ui(() => { Running = null; _window = IntPtr.Zero; SessionChanged?.Invoke(); Notify?.Invoke($"Couldn't detect {g.Name} starting — check its launcher. Gaming Hub stopped tracking it."); });
            return;
        }
        var played = DateTime.UtcNow - started;
        try { repo.AddPlaytime(g.Id, played, DateTime.UtcNow, verified ? target?.Identity : null); }
        catch (Exception ex) { log.LogError(ex, "Could not save playtime for {Game}", g.Name); }
        log.LogInformation("{Game} session ended after {Played}; placement verified={Verified}", g.Name, played, verified);
        helper.StopIfStartedByUs(ownedHelper);
        Ui(() =>
        {
            Running = null;
            DetectedSessionStartUtc = null;
            _window = IntPtr.Zero;
            SessionChanged?.Invoke();
            Exited?.Invoke(g);
        });
    }

    /// <summary>Folder-based tracking only for real game folders: never Windows, Program Files roots or drive roots.</summary>
    static string? TrackableDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        var full = Path.GetFullPath(dir).TrimEnd('\\');
        string[] broad =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ];
        bool tooBroad = Path.GetPathRoot(full)!.TrimEnd('\\') == full ||
            broad.Any(b => full.Equals(b, StringComparison.OrdinalIgnoreCase) || full.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase));
        return tooBroad ? null : full;
    }

    static HashSet<int> Pids(string? dir, Process? proc)
    {
        var set = dir is not null && Directory.Exists(dir) ? GameWindows.ProcessesUnder(dir) : [];
        try { if (proc is not null && !proc.HasExited) set.Add(proc.Id); } catch { }
        return set;
    }

    async Task<bool> PlaceAsync(Game g, IntPtr w, DisplayInfo target)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var bounds = WindowPlacement.GetBounds(w);
            var plan = PlacementPlanner.Plan(bounds, WindowPlacement.GetHostMonitorBounds(w), target, GameWindows.ExclusiveFullscreenActive(w));
            log.LogInformation("{Game} placement attempt {N}: {Action} {Target} ({Reason}); window {Bounds}", g.Name, attempt, plan.Action, plan.Target, plan.Reason, bounds);
            if (plan.Action == PlacementAction.None) return true;
            if (plan.Action == PlacementAction.Unsupported)
            {
                Ui(() => Notify?.Invoke($"{g.Name}: {plan.Reason}"));
                return false;
            }
            bool ok = WindowPlacement.Move(w, plan.Target);
            await Task.Delay(800);
            var after = WindowPlacement.GetBounds(w);
            if (PlacementPlanner.IsOn(after, target.Bounds))
            {
                log.LogInformation("{Game} placement verified on {Display}: {Bounds}", g.Name, target.Identity.FriendlyName, after);
                Ui(() => Notify?.Invoke($"{g.Name} moved to {target.Identity.FriendlyName}"));
                return true;
            }
            if (!ok) log.LogWarning("{Game}: SetWindowPos refused (error {Err}) — game may run elevated or be protected", g.Name, System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
        Ui(() => Notify?.Invoke($"Couldn't move {g.Name} to {target.Identity.FriendlyName}. Pick the monitor in the game's display settings, use borderless/windowed mode, or try Move Game to Display."));
        return false;
    }

    /// <summary>Manual "Move Game to Display" for compatible (windowed/borderless) games.</summary>
    /// <summary>The tracked game's main window (zero until found).</summary>
    public IntPtr RunningWindow => _window;


    string? _dir;
    List<int> _paused = [];
    System.IO.Pipes.NamedPipeClientStream? _companion;   // open while an administrator game is paused by its companion
    StreamReader? _companionIn;
    StreamWriter? _companionOut;
    public bool IsPaused => _paused.Count > 0 || _companion is not null;

    /// <summary>The running game's processes as Gaming Hub sees them (its folder plus its window's process).</summary>
    HashSet<int> GamePids()
    {
        var pids = _dir is not null ? GameWindows.ProcessesUnder(_dir) : [];
        if (_window != IntPtr.Zero && GetWindowThreadProcessId(_window, out int wpid) != 0) pids.Add(wpid);
        pids.Remove(Environment.ProcessId);
        return pids;
    }

    /// <summary>One request to the game's administrator companion (D-019). Keeps the connection when <paramref name="keep"/>
    /// (a pause lasts while it is open: if Gaming Hub goes away, the companion resumes the game). Null when not reachable.</summary>
    string? AskCompanion(Game g, string request, bool keep)
    {
        try
        {
            var c = _companion ?? new System.IO.Pipes.NamedPipeClientStream(".", ElevatedTask.PipeName(g.Id, App.IsDev), System.IO.Pipes.PipeDirection.InOut);
            if (!c.IsConnected) { c.Connect(700); _companionIn = new StreamReader(c); _companionOut = new StreamWriter(c) { AutoFlush = true }; }
            _companionOut!.WriteLine(request);
            var reply = _companionIn!.ReadLine();
            if (keep) _companion = c; else { c.Dispose(); _companion = null; }
            return reply;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Companion for {Game} not reachable for {Request}: {Err}", g.Name, request, ex.Message);
            _companion?.Dispose(); _companion = null;
            return null;
        }
    }

    /// <summary>Pauses the running game (all its processes) so it ignores the controller while the quick menu is open.
    /// Administrator games are paused by their companion. Returns null when paused, else why not.</summary>
    public string? PauseGame()
    {
        if (Running is not { } g || IsPaused) return null;
        if (g.RunElevated)
        {
            var r = AskCompanion(g, "pause", keep: true);
            log.LogInformation("Pause {Game} via companion: {Reply}", g.Name, r ?? "unreachable");
            if (r == "ok") return null;
            ResumeGame();
            return r switch
            {
                null => $"{g.Name} can't be paused — start it again from Gaming Hub after this update",
                _ when r.StartsWith("refused:") => $"{g.Name} can't be paused: it protects itself (anti-cheat) — it keeps running",
                _ => $"{g.Name}'s process wasn't found",
            };
        }
        var pids = GamePids();
        if (pids.Count == 0) return $"{g.Name}'s process wasn't found";
        var (paused, refused) = ProcessFreezer.SuspendAll(pids);
        _paused = paused;
        log.LogInformation("Pause {Game}: {Done}/{All} processes; refused: {Refused}", g.Name, paused.Count, pids.Count, string.Join(", ", refused));
        return refused.Count == 0 ? null : $"{g.Name} can't be paused: it runs as administrator or protects itself (anti-cheat) — it keeps running";
    }

    /// <summary>Resumes a game paused by <see cref="PauseGame"/>. Safe to call any time.</summary>
    public void ResumeGame()
    {
        if (_companion is not null && Running is { } g)
        {
            AskCompanion(g, "resume", keep: false);   // closing the connection resumes it too
            log.LogInformation("Resumed {Game} via companion", g.Name);
        }
        _companion?.Dispose(); _companion = null;
        if (_paused.Count == 0) return;
        ProcessFreezer.Resume(_paused);
        log.LogInformation("Resumed {Game} ({Count} processes)", Running?.Name, _paused.Count);
        _paused = [];
    }

    /// <summary>Force-closes the running game, like Task Manager's End task (the quick menu asks twice first). Administrator
    /// games are closed by their companion. Unsaved progress is lost.</summary>
    public string ForceCloseGame()
    {
        if (Running is not { } g) return "No game is running";
        string? reply;
        if (g.RunElevated)
        {
            ResumeGame();
            reply = AskCompanion(g, "kill", keep: false);
        }
        else
        {
            ResumeGame();
            reply = ElevatedCompanion.ForceClose(GamePids()) > 0 ? "ok" : "none";
        }
        log.LogInformation("Force-close {Game}: {Reply}", g.Name, reply ?? "companion unreachable");
        return reply switch
        {
            "ok" => $"Closed {g.Name}",
            null => $"{g.Name} runs as administrator and was started before this update — close it from the game, or start it again from Gaming Hub",
            _ => $"{g.Name}'s process wasn't found",
        };
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);

    /// <summary>Brings the running game's window back to the front (restoring it if minimized).</summary>
    public string? ReturnToGame()
    {
        if (Running is not { } g) return "No game is running";
        if (_window == IntPtr.Zero || !GameWindows.IsWindow(_window)) return $"{g.Name}'s window hasn't been found yet";
        GameWindows.ShowAndForeground(_window);
        log.LogInformation("Returned to {Game}", g.Name);
        return null;
    }

    public async Task<string> MoveRunningGameAsync(DisplayInfo target)
    {
        if (Running is not { } g || _window == IntPtr.Zero) return "No game window to move";
        return await PlaceAsync(g, _window, target) ? $"Moved {g.Name}" : $"{g.Name} could not be moved";
    }
}

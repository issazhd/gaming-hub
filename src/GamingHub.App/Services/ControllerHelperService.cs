using System.ComponentModel;
using System.Diagnostics;
using GamingHub.Core.Games;
using GamingHub.Core.Settings;
using GamingHub.Infrastructure.Games;
using GamingHub.Infrastructure.Discovery;
using Microsoft.Extensions.Logging;

namespace GamingHub.App.Services;

/// <summary>A controller helper Gaming Hub can start for games that need it (user-installed; Gaming Hub installs no drivers).</summary>
/// <param name="StartArgs">Arguments used when Gaming Hub starts it.</param>
/// <param name="ShutdownArgs">Arguments that make a running copy exit cleanly, or null when it should be left running.</param>
/// <param name="StartDelayS">Time for its virtual controller to appear before the game starts.</param>
public sealed record HelperKind(string Name, string ProcessName, string StartArgs, string? ShutdownArgs, int StartDelayS, string Purpose)
{
    public string ExeName => ProcessName + ".exe";

    public static readonly HelperKind ZerkSense5 = new("ZerkSense5", "ZerkSense5", "", null, 6,
        "Enables haptics, adaptive triggers and the speaker on a Bluetooth DualSense, or emulates an Xbox 360 controller");
    public static readonly HelperKind Ds4Windows = new("DS4Windows", "DS4Windows", "-m", "-command Shutdown", 4,
        "Emulates an Xbox controller for games that only support Xbox controllers");
    public static readonly HelperKind Nexus = new("DS4Windows Nexus", "DS4Windows", "", null, 4,
        "Starts Nexus with your configured controller profile");
    public static readonly HelperKind[] All = [ZerkSense5, Ds4Windows, Nexus];

    public override string ToString() => Name;   // shown by the Settings dropdown
}

/// <summary>
/// Starts the chosen controller helper (ZerkSense5 or DS4Windows) for games flagged in Game settings. DS4Windows is closed
/// again afterwards if Gaming Hub started it; ZerkSense5 runs elevated in the tray and handles per-game profiles itself,
/// so it is only ever started, never closed (a normal app cannot close an administrator app anyway).
/// </summary>
public sealed class ControllerHelperService(SettingsService settings, ILogger<ControllerHelperService> log)
{
    ControllerSettings S => settings.Current.Controller;

    /// <summary>Chosen helper; when not chosen yet: ZerkSense5 if it is installed, else DS4Windows.</summary>
    public HelperKind Current =>
        HelperKind.All.FirstOrDefault(h => h.Name == S.Helper) ?? (FindExe(HelperKind.ZerkSense5) is not null ? HelperKind.ZerkSense5 : HelperKind.Ds4Windows);

    /// <summary>The helper a game uses: its own choice, else the one chosen in Settings.</summary>
    public HelperKind For(GamingHub.Core.Games.Game g) => ByName(g.HelperName) ?? Current;

    public static HelperKind? ByName(string? name) => HelperKind.All.FirstOrDefault(h => h.Name == name);

    static bool Matches(HelperKind h, string? path)
    {
        if (!File.Exists(path)) return false;
        if (h == HelperKind.ZerkSense5) return true;
        try
        {
            var product = FileVersionInfo.GetVersionInfo(path).ProductName;
            return string.Equals(product, h.Name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { return false; }
    }

    static string? RunningPath(HelperKind h)
    {
        string? match = null;
        foreach (var proc in Process.GetProcessesByName(h.ProcessName))
            using (proc)
            {
                var path = GameWindows.ProcessPath(proc.Id);
                if (Matches(h, path)) match = path;
            }
        return match;
    }

    static bool AnyProcessNamed(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var process in processes) process.Dispose();
        return processes.Length != 0;
    }

    public static bool IsRunning(HelperKind h) => h == HelperKind.ZerkSense5
        ? AnyProcessNamed(h.ProcessName) : RunningPath(h) is not null;

    string? ConfiguredPath(HelperKind h) => h == HelperKind.Nexus ? S.Ds4NexusPath :
        h == HelperKind.ZerkSense5 ? S.ZerkSense5Path : S.Ds4WindowsPath;

    /// <summary>Settings path, else a running copy, else common install/download folders.</summary>
    public string? FindExe(HelperKind h)
    {
        if (ConfiguredPath(h) is { } p && Matches(h, p)) return p;
        if (RunningPath(h) is { } running) return running;
        string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var home = Env(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { Path.Combine(Env(Environment.SpecialFolder.LocalApplicationData), "Programs", h.Name),
                     Path.Combine(Env(Environment.SpecialFolder.ProgramFiles), h.Name),
                     Path.Combine(Env(Environment.SpecialFolder.LocalApplicationData), h.Name),
                     Env(Environment.SpecialFolder.DesktopDirectory), Path.Combine(home, "Downloads"), Path.Combine(home, "Documents") })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
                var hit = Directory.EnumerateFiles(root, h.ExeName, options).FirstOrDefault(f => Matches(h, f));
                if (hit is not null) return hit;
                if (h.ProcessName == "DS4Windows" && root == Env(Environment.SpecialFolder.DesktopDirectory))
                    foreach (var shortcut in Directory.EnumerateFiles(root, "*.lnk", options))
                        if (ShortcutScanner.ReadLink(shortcut)?.Target is { } target &&
                            Path.GetFileName(target).Equals(h.ExeName, StringComparison.OrdinalIgnoreCase) && Matches(h, target)) return target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public bool SetPath(HelperKind h, string path)
    {
        if (!Matches(h, path)) return false;
        if (h == HelperKind.Nexus) S.Ds4NexusPath = path;
        else if (h == HelperKind.ZerkSense5) S.ZerkSense5Path = path;
        else S.Ds4WindowsPath = path;
        settings.Save();
        return true;
    }

    /// <summary>Starts the helper if needed and waits for its virtual controller. Returns true when Gaming Hub started it.</summary>
    public async Task<bool> EnsureRunningAsync(HelperKind h)
    {
        if (IsRunning(h)) return false;
        if (h.ProcessName == "DS4Windows" && AnyProcessNamed(h.ProcessName))
        {
            throw new InvalidOperationException($"Another DS4Windows instance is open. Exit it from the tray before starting {h.Name}.");
        }
        var exe = FindExe(h) ?? throw new FileNotFoundException($"{h.Name} wasn't found. Set its location in Settings › Controller.");
        try
        {
            if (NoPromptReady(h, exe))
            {
                if (ElevatedTasks.Start(TaskName(h)) is { } err) throw new InvalidOperationException($"Couldn't start {h.Name}: {err}");
                log.LogInformation("Starting {Helper} through its admin task (no prompt)", h.Name);
            }
            else Process.Start(new ProcessStartInfo(exe, h.StartArgs) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // the Windows admin prompt was declined
        {
            throw new InvalidOperationException($"{h.Name} needs administrator approval to start. Start it once yourself (it stays in the tray), then play.");
        }
        log.LogInformation("Started controller helper {Helper} from {Exe}", h.Name, exe);
        await Task.Delay(TimeSpan.FromSeconds(h.StartDelayS));   // virtual controller must exist before the game probes for pads
        if (h == HelperKind.Nexus && !IsRunning(h))
            throw new InvalidOperationException("Nexus did not stay open. Open DS4Windows Nexus, finish its setup, then try again.");
        return true;
    }

    // ---- Start without the UAC prompt (D-015): a Task Scheduler task with admin rights, set up once with approval ----
    static string TaskName(HelperKind h) => ElevatedTask.HelperName(h.Name, App.IsDev);

    static TaskAction ActionFor(HelperKind h, string exe) => new(exe, h.StartArgs, Path.GetDirectoryName(exe)!);

    /// <summary>True when the helper's admin task exists and starts the current executable.</summary>
    public bool NoPromptReady(HelperKind h, string? exe = null) =>
        (exe ?? FindExe(h)) is { } e && ElevatedTask.Matches(ElevatedTasks.Query(TaskName(h)), ActionFor(h, e));

    /// <summary>Creates or removes the helper's admin task (one Windows prompt). Returns null on success, else a message.</summary>
    public string? SetNoPrompt(HelperKind h, bool on)
    {
        string? err;
        if (!on) err = ElevatedTasks.Delete(TaskName(h));
        else if (FindExe(h) is not { } exe) err = $"{h.Name} wasn't found. Use Locate first.";
        else err = ElevatedTasks.Create(TaskName(h), ElevatedTask.Xml(ActionFor(h, exe), $"{Environment.UserDomainName}\\{Environment.UserName}", h.Name));
        log.LogInformation("{Helper} no-prompt start {State}: {Result}", h.Name, on ? "on" : "off", err ?? "ok");
        return err;
    }

    /// <summary>Closes the helper after the game — only helpers that support a clean exit (DS4Windows).</summary>
    public void StopIfStartedByUs(HelperKind h, bool startedByUs)
    {
        if (!startedByUs || h.ShutdownArgs is null || !IsRunning(h) || FindExe(h) is not { } exe) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe, h.ShutdownArgs) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
            log.LogInformation("Asked {Helper} to shut down", h.Name);
        }
        catch (Exception ex) { log.LogWarning(ex, "Couldn't close {Helper}", h.Name); }
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using GamingHub.Core.Displays;
using GamingHub.Core.Games;
using GamingHub.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GamingHub.App.Services;

/// <summary>User-level system integration: start with Windows (HKCU Run), diagnostics export, backup/restore.</summary>
public sealed class SystemService(SqliteStore store, IDisplayProvider displays, IGameRepository games, SettingsService settings, ILogger<SystemService> log)
{
    // Legacy (≤ 1.0.1) registration, cleaned up when found: the Run value did not always appear in Task Manager.
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "GamingHub",
        ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    static string Exe => Environment.ProcessPath!;
    static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    /// <summary>Shortcut in the user's Startup folder — always listed in Task Manager / Settings › Startup apps.</summary>
    static string StartupShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Gaming Hub.lnk");

    /// <summary>True when Windows will start Gaming Hub at sign-in (set by this app or by the installer).</summary>
    public bool IsStartupRegistered()
    {
        if (App.IsDev) return false;   // the development build never reads or changes the startup entry
        return IsStartupRegisteredCore();
    }

    bool IsStartupRegisteredCore()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        bool legacy = k?.GetValue(RunName) is not null;
        if (legacy) ApplyStartupCore(true);   // migrate the old Run entry to the Startup-folder shortcut
        return legacy || File.Exists(StartupShortcut);
    }

    public void ApplyStartup(bool enabled)
    {
        if (App.IsDev) { log.LogInformation("Development build: not changing start-with-Windows"); return; }
        ApplyStartupCore(enabled);
    }

    void ApplyStartupCore(bool enabled)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) if (k.GetValue(RunName) is not null) k.DeleteValue(RunName);
            using (var a = Registry.CurrentUser.CreateSubKey(ApprovedKey)) if (a.GetValue(RunName) is not null) a.DeleteValue(RunName);
            if (!enabled) { if (File.Exists(StartupShortcut)) File.Delete(StartupShortcut); return; }
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic lnk = shell.CreateShortcut(StartupShortcut);
            lnk.TargetPath = Exe;
            lnk.Arguments = "--startup";
            lnk.WorkingDirectory = Path.GetDirectoryName(Exe);
            lnk.Description = "Gaming Hub (starts in the tray; PS opens it)";
            lnk.Save();
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not update startup registration"); }
    }

    public void OpenDataFolder() => Process.Start(new ProcessStartInfo(App.DataDir) { UseShellExecute = true });

    public string ExportDiagnostics()
    {
        try
        {
            var path = Path.Combine(Documents, $"GamingHub-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            var json = new JsonSerializerOptions { WriteIndented = true };
            void Add(string name, string text) { using var w = new StreamWriter(zip.CreateEntry(name).Open()); w.Write(text); }
            Add("displays.json", JsonSerializer.Serialize(displays.GetSnapshot(), json));
            Add("settings.json", JsonSerializer.Serialize(settings.Current, json));
            Add("games.json", JsonSerializer.Serialize(games.GetAll().Select(g => new { g.Name, g.Source, g.ExternalId, g.InstallDir, g.TargetMode, g.AutoPlace, g.Installed, g.PlaytimeSeconds }), json));
            using (var p = Process.GetCurrentProcess())
                Add("resources.txt", $"Working set: {p.WorkingSet64 / 1048576} MB\nPrivate: {p.PrivateMemorySize64 / 1048576} MB\nCPU time: {p.TotalProcessorTime}\nThreads: {p.Threads.Count}\nUptime: {DateTime.Now - p.StartTime}\nOS: {Environment.OSVersion}\n.NET: {Environment.Version}");
            foreach (var f in Directory.EnumerateFiles(Path.Combine(App.DataDir, "logs")).OrderByDescending(File.GetLastWriteTime).Take(3))
            {
                using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);   // today's log is open
                using var dst = zip.CreateEntry("logs/" + Path.GetFileName(f)).Open();
                src.CopyTo(dst);
            }
            log.LogInformation("Diagnostics exported to {Path}", path);
            return $"Diagnostics saved to {path}";
        }
        catch (Exception ex) { log.LogError(ex, "Diagnostics export failed"); return $"Export failed: {ex.Message}"; }
    }


    [System.Runtime.InteropServices.DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    /// <summary>Power menu actions — only ever from the user's explicit choice in the power menu.</summary>
    public void Power(string kind)
    {
        log.LogInformation("Power action: {Kind}", kind);
        switch (kind)
        {
            case "sleep": SetSuspendState(false, false, false); break;
            case "screenoff": PostMessage(new IntPtr(0xFFFF), 0x0112, new IntPtr(0xF170), new IntPtr(2)); break;   // WM_SYSCOMMAND SC_MONITORPOWER off
            case "restart": Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false }); break;
            case "shutdown": Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false }); break;
        }
    }
    public string Backup()
    {
        try
        {
            var path = Path.Combine(Documents, $"GamingHub-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            store.BackupTo(path);
            return $"Backup saved to {path}";
        }
        catch (Exception ex) { log.LogError(ex, "Backup failed"); return $"Backup failed: {ex.Message}"; }
    }

    /// <summary>Restores a backup and restarts the app so all services reload it.</summary>
    public string? Restore()
    {
        var dlg = new OpenFileDialog { Filter = "Gaming Hub backup (*.db)|*.db", InitialDirectory = Documents, Title = "Restore Gaming Hub backup" };
        if (dlg.ShowDialog() != true) return null;
        try
        {
            store.RestoreFrom(dlg.FileName);
            log.LogInformation("Restored backup {Path}; restarting", dlg.FileName);
            App.Restart();
            return null;
        }
        catch (Exception ex) { log.LogError(ex, "Restore failed"); return $"Restore failed: {ex.Message}"; }
    }
}

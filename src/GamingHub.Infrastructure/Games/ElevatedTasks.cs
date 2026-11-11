using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace GamingHub.Infrastructure.Games;

/// <summary>
/// Task Scheduler wrapper (schtasks.exe) for "run as administrator without a prompt".
/// Creating or deleting a task shows one UAC prompt; querying and starting it don't.
/// </summary>
public static class ElevatedTasks
{
    /// <summary>Creates or replaces the task (UAC prompt). Returns null on success, else a message.</summary>
    public static string? Create(string name, string xml)
    {
        var file = Path.Combine(Path.GetTempPath(), $"GamingHub-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            return Elevated($"/create /tn \"{name}\" /xml \"{file}\" /f");
        }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>Removes the task (UAC prompt). Missing tasks count as removed.</summary>
    public static string? Delete(string name) => Query(name) is null ? null : Elevated($"/delete /tn \"{name}\" /f");

    /// <summary>The task's XML, or null when it doesn't exist.</summary>
    public static string? Query(string name)
    {
        var (code, output) = Run($"/query /tn \"{name}\" /xml");
        return code == 0 ? output : null;
    }

    /// <summary>Starts the task (no prompt). Returns null on success, else schtasks' message.</summary>
    public static string? Start(string name)
    {
        var (code, output) = Run($"/run /tn \"{name}\"");
        return code == 0 ? null : output.Trim();
    }

    static (int, string) Run(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
        return (p.HasExited ? p.ExitCode : -1, outTask.Result + err);
    }

    static string? Elevated(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            p.WaitForExit(30_000);
            return p.ExitCode == 0 ? null : $"Task Scheduler reported error {p.ExitCode}";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "The Windows prompt was declined"; }
        catch (Win32Exception ex) { return ex.Message; }
    }
}

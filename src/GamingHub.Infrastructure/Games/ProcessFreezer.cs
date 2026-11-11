using System.Runtime.InteropServices;

namespace GamingHub.Infrastructure.Games;

/// <summary>
/// Pauses (suspends) and resumes whole processes — used to pause a game while the quick menu is open, so it can't
/// react to the controller. Works only on processes running with the same rights as Gaming Hub (not administrator ones).
/// </summary>
public static class ProcessFreezer
{
    const uint PROCESS_SUSPEND_RESUME = 0x0800;

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr h);

    /// <summary>Suspends each process; returns the ones that were actually suspended (to resume later).</summary>
    public static List<int> Suspend(IEnumerable<int> pids)
    {
        var done = new List<int>();
        foreach (var pid in pids.Distinct())
        {
            var h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
            if (h == IntPtr.Zero) continue;   // administrator process or already gone
            try { if (NtSuspendProcess(h) == 0) done.Add(pid); }
            finally { CloseHandle(h); }
        }
        return done;
    }

    /// <summary>All or nothing: suspends every process, or — if any one refuses (administrator process, or protected by an
    /// anti-cheat driver) — resumes the ones already suspended. Returns the suspended pids (empty on failure) and the names
    /// of the processes that refused.</summary>
    public static (List<int> Paused, List<string> Refused) SuspendAll(IEnumerable<int> pids)
    {
        var all = pids.Distinct().ToList();
        var done = Suspend(all);
        var refused = all.Except(done).Select(Name).Where(n => n is not null).Select(n => n!).ToList();
        if (refused.Count == 0) return (done, refused);
        Resume(done);
        return ([], refused);
    }

    static string? Name(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName; }
        catch (ArgumentException) { return null; }   // already exited: nothing to pause
    }

    public static void Resume(IEnumerable<int> pids)
    {
        foreach (var pid in pids)
        {
            var h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
            if (h == IntPtr.Zero) continue;
            try { NtResumeProcess(h); }
            finally { CloseHandle(h); }
        }
    }
}

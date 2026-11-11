using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GamingHub.Core.Displays;

namespace GamingHub.Infrastructure.Games;

/// <summary>Finds a launched game's processes and its real top-level window (not a splash or platform launcher).</summary>
public static class GameWindows
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80;
    const uint GW_OWNER = 4;

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern long GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    public static string? ProcessPath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;   // protected / anti-cheat processes may refuse
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>PIDs whose executable lives under <paramref name="dir"/> (handles multi-process games and launcher hand-offs).</summary>
    public static HashSet<int> ProcessesUnder(string dir)
    {
        var root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        var set = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id <= 4) continue;
                var path = ProcessPath(p.Id);
                if (path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) set.Add(p.Id);
            }
        }
        return set;
    }

    /// <summary>Largest visible, unowned, titled, non-tool window of the given processes (≥ 640×360).</summary>
    public static IntPtr FindMainWindow(IReadOnlySet<int> pids)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out int pid);
            if (!pids.Contains(pid) || !IsWindowVisible(h) || IsIconic(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            if ((GetWindowLongPtr(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0 || GetWindowTextLength(h) == 0) return true;
            if (IsDialog(h)) return true;   // error/message boxes are not the game window
            var r = Displays.WindowPlacement.GetBounds(h);
            long area = (long)r.Width * r.Height;
            if (r.Width >= 640 && r.Height >= 360 && area > bestArea) { best = h; bestArea = area; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>True when a Direct3D exclusive-fullscreen app is in the foreground (QUNS_RUNNING_D3D_FULL_SCREEN).</summary>
    public static bool ExclusiveFullscreenActive(IntPtr gameWindow) =>
        GetForegroundWindow() == gameWindow && SHQueryUserNotificationState(out int s) == 0 && s == 3;

    /// <summary>True when a fullscreen app or game is in front (QUNS_BUSY / QUNS_RUNNING_D3D_FULL_SCREEN).</summary>
    /// <summary>Process name of the foreground window (e.g. "steamwebhelper"), or null.</summary>
    public static string? ForegroundProcessName()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out int pid);
        try { return pid == 0 ? null : Process.GetProcessById(pid).ProcessName; } catch (ArgumentException) { return null; }
    }

    /// <summary>Direct3D exclusive fullscreen in front: nothing can be drawn over it safely.</summary>
    public static bool ExclusiveFullscreenInFront() => SHQueryUserNotificationState(out int s) == 0 && s == 3;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    public static string? WindowTitle(IntPtr window)
    {
        var title = new StringBuilder(256);
        return GetWindowText(window, title, title.Capacity) > 0 ? title.ToString() : null;
    }

    public static int WindowProcessId(IntPtr window)
    {
        GetWindowThreadProcessId(window, out int pid);
        return pid;
    }

    public static IntPtr Foreground() => GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int max);

    /// <summary>Standard Windows dialog (message box, error box): class "#32770".</summary>
    public static bool IsDialog(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(32);
        return GetClassName(h, sb, sb.Capacity) > 0 && sb.ToString() == "#32770";
    }

    public static bool FullscreenAppInFront() => SHQueryUserNotificationState(out int s) == 0 && s is 2 or 3;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    /// <summary>Windows only lets the app that received the last input take the foreground; controller (HID) input
    /// doesn't count. Attach to the foreground thread's input queue, and fall back to a no-op Alt tap.</summary>
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

    /// <summary>Restores the window if it is minimized, then brings it to the front.</summary>
    public static void ShowAndForeground(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        ForceForeground(hwnd);
    }

    public static void ForceForeground(IntPtr hwnd)
    {
        var fg = GetForegroundWindow();
        if (fg == hwnd) return;
        uint fgThread = GetWindowThreadProcessId(fg, out _), me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        BringWindowToTop(hwnd);
        bool ok = SetForegroundWindow(hwnd);
        if (attached) AttachThreadInput(me, fgThread, false);
        if (ok && GetForegroundWindow() == hwnd) return;
        keybd_event(0x12, 0, 0, IntPtr.Zero);       // VK_MENU down
        keybd_event(0x12, 0, 2, IntPtr.Zero);       // VK_MENU up
        SetForegroundWindow(hwnd);
    }
}

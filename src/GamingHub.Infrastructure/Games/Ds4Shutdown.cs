using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GamingHub.Infrastructure.Games;

/// <summary>Deliver DS4Windows' shutdown message to the owned process, including its hidden tray window.</summary>
public static class Ds4Shutdown
{
    delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder title, int count);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct CopyData { public IntPtr Tag; public int Length; [MarshalAs(UnmanagedType.LPStr)] public string Text; }
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr sender,
        ref CopyData data, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);

    public static bool Request(Process owned)
    {
        IntPtr target = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out int pid);
            if (pid != owned.Id) return true;
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (!title.ToString().StartsWith("DS4Windows", StringComparison.OrdinalIgnoreCase)) return true;
            target = window;
            return false;
        }, IntPtr.Zero);
        if (target == IntPtr.Zero || owned.HasExited) return false;
        var command = new CopyData { Text = "shutdown", Length = 8 };
        SendMessageTimeout(target, 0x004A, IntPtr.Zero, ref command, 2, 1500, out _);
        if (owned.WaitForExit(1500)) return true;
        GetWindowThreadProcessId(target, out int currentPid);
        return currentPid == owned.Id && !owned.HasExited && PostMessage(target, 0x0010, IntPtr.Zero, IntPtr.Zero);
    }
}

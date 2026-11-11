using System.Runtime.InteropServices;
using GamingHub.Core.Displays;
using static GamingHub.Infrastructure.Displays.NativeDisplay;

namespace GamingHub.Infrastructure.Displays;

/// <summary>Physical-pixel window placement + verification (process must be Per-Monitor V2 aware).</summary>
public static class WindowPlacement
{
    const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);

    public static bool Move(IntPtr hwnd, PixelRect r) =>
        SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

    public static PixelRect GetBounds(IntPtr hwnd) =>
        GetWindowRect(hwnd, out var r) ? new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : default;

    /// <summary>GDI device name (\\.\DISPLAYn) of the monitor hosting most of the window.</summary>
    public static string? GetHostGdiName(IntPtr hwnd)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi) ? mi.szDevice : null;
    }

    /// <summary>Current DPI of the monitor hosting most of the window (live — follows a game's resolution change).</summary>
    public static uint GetHostMonitorDpi(IntPtr hwnd) =>
        NativeDisplay.GetDpiForMonitor(MonitorFromWindow(hwnd, 2), 0, out var dx, out _) == 0 ? dx : 96;

    /// <summary>Bounds of the monitor hosting most of the window.</summary>
    public static PixelRect GetHostMonitorBounds(IntPtr hwnd)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)
            ? new(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top)
            : default;
    }

    /// <summary>True when the window covers the target bounds (fullscreen) or lies mostly on it (windowed).</summary>
    public static bool IsOn(IntPtr hwnd, PixelRect target, bool exact)
    {
        var b = GetBounds(hwnd);
        if (exact) return b == target;
        long area = (long)b.Width * b.Height;
        return area > 0 && target.IntersectionArea(b) * 2 > area;
    }
}

using GamingHub.Core.Displays;

namespace GamingHub.Core.Games;

public enum PlacementAction { None, Move, Unsupported }

public sealed record PlacementPlan(PlacementAction Action, PixelRect Target, string Reason);

/// <summary>Decides how (and whether) to put a game window on the target display. Pure logic; Win32 applies it.</summary>
public static class PlacementPlanner
{
    /// <param name="window">Current window bounds (physical px).</param>
    /// <param name="currentMonitor">Bounds of the monitor currently hosting the window.</param>
    public static PlacementPlan Plan(PixelRect window, PixelRect currentMonitor, DisplayInfo target, bool exclusiveFullscreen)
    {
        if (IsOn(window, target.Bounds))
            return new(PlacementAction.None, window, "already on target display");
        if (exclusiveFullscreen)
            return new(PlacementAction.Unsupported, window,
                "exclusive fullscreen cannot be moved; choose the display in the game's own settings or use borderless/windowed mode");

        // Borderless fullscreen: window covers its whole monitor → cover the target monitor.
        if (window.X <= currentMonitor.X && window.Y <= currentMonitor.Y &&
            window.Right >= currentMonitor.Right && window.Bottom >= currentMonitor.Bottom)
            return new(PlacementAction.Move, target.Bounds, "borderless fullscreen → target monitor bounds");

        // Windowed: keep size (clamped to work area), center on target work area.
        var wa = target.WorkArea.Width > 0 ? target.WorkArea : target.Bounds;
        int w = Math.Min(window.Width, wa.Width), h = Math.Min(window.Height, wa.Height);
        return new(PlacementAction.Move, new PixelRect(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h),
            "windowed → centered on target work area");
    }

    /// <summary>Placement is verified when most of the window lies on the target display.</summary>
    public static bool IsOn(PixelRect window, PixelRect display)
    {
        long area = (long)window.Width * window.Height;
        return area > 0 && display.IntersectionArea(window) * 2 > area;
    }
}

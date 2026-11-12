using System.Windows;
using System.Windows.Threading;
using GamingHub.Core.Displays;
using GamingHub.Core.Settings;
using GamingHub.Infrastructure.Displays;
using Microsoft.Extensions.Logging;

namespace GamingHub.App.Services;

public sealed class SettingsService(ISettingsStore store)
{
    public AppSettings Current { get; } = store.Load();
    public void Save() => store.Save(Current);
}

/// <summary>Keeps the launcher window on the display chosen by <see cref="LauncherDisplayPolicy"/>, without restarting.</summary>
public sealed class LauncherPlacementService(DisplayWatcher watcher, SettingsService settings, ILogger<LauncherPlacementService> log)
{
    Window? _window;
    IntPtr _hwnd;
    DisplayInfo? _testOverride;
    bool? _prefWasAvailable;
    long _goPreferredUntil;
    bool _restoreFocus, _notifyExtended;

    /// <summary>The user asked to extend to the preferred display: move there once it becomes active (within 20 s).</summary>
    public void GoToPreferredSoon(bool restoreFocus, bool notifyExtended = true)
    {
        _goPreferredUntil = Environment.TickCount64 + 20_000;
        _restoreFocus = restoreFocus;
        _notifyExtended = notifyExtended;
    }
    public bool IsHubActive => _window?.IsActive == true;
    public event Action? RestoreInputFocus;

    /// <summary>The preferred display became active after Gaming Hub extended the desktop to it.</summary>
    public event Action<DisplayInfo>? ExtendedToPreferred;

    public DisplayInfo? HostDisplay { get; private set; }
    // Resolve the physical window now; HostDisplay records the placement target.
    public DisplayInfo? VisibleHost
    {
        get
        {
            if (_hwnd == IntPtr.Zero || _window?.IsVisible != true || _window.WindowState == WindowState.Minimized) return null;
            var name = WindowPlacement.GetHostGdiName(_hwnd);
            return name is null ? null : watcher.Current.Active.FirstOrDefault(d =>
                string.Equals(d.GdiName, name, StringComparison.OrdinalIgnoreCase));
        }
    }
    public LauncherDecision? LastDecision { get; private set; }
    public bool IsTesting => _testOverride is not null;
    /// <summary>True while a game is running: suppresses auto-opening the launcher.</summary>
    public Func<bool> SuppressActivation { get; set; } = () => false;
    public event Action<string>? Notify;
    public event Action? StateChanged;

    LauncherDisplaySettings S => settings.Current.Display;

    public void Attach(Window w, IntPtr hwnd)
    {
        (_window, _hwnd) = (w, hwnd);
        watcher.Changed += (_, reason) => Evaluate(reason);
        w.StateChanged += (_, _) => { if (w.WindowState != WindowState.Minimized) Evaluate("restored"); };
        w.IsVisibleChanged += (_, _) => { if (w.IsVisible) Evaluate("shown"); };
        Evaluate("startup");
    }

    public void Evaluate(string reason)
    {
        if (_window is null) return;
        var snap = watcher.Current;
        var host = HostDisplay is null ? null : DisplayMatcher.Match(HostDisplay.Identity, snap.Active).Display;
        var d = LauncherDisplayPolicy.Decide(S, snap, reason == "startup" ? null : host);
        LastDecision = d;

        if (_prefWasAvailable is { } was && was != d.PreferredAvailable && S.Preferred is not null)
        {
            var name = S.Preferred.FriendlyName;
            if (d.PreferredAvailable)
            {
                bool moving = S.AutoSwitchToPreferred || Environment.TickCount64 < _goPreferredUntil;
                if (S.ConnectionNotifications)
                    Notify?.Invoke(moving ? $"{name} connected — moving Gaming Hub there" : $"{name} connected (auto-switch is off in Settings › Display)");
                // Never pop the launcher over a running game.
                if (S.AutoOpenOnPreferredConnect && !SuppressActivation() && (_window.WindowState == WindowState.Minimized || !_window.IsVisible))
                {
                    _window.Show();
                    _window.WindowState = WindowState.Normal;
                }
            }
            else
            {
                if (S.ConnectionNotifications) Notify?.Invoke($"{name} disconnected — using {d.Target?.Identity.FriendlyName ?? "fallback"}");
                if (S.CloseOnDisconnect) { log.LogInformation("Closing: preferred display disconnected"); Application.Current.Shutdown(); return; }
            }
        }
        _prefWasAvailable = d.PreferredAvailable;

        if (_testOverride is not null && DisplayMatcher.Match(_testOverride.Identity, snap.Active).Display is null)
        {
            _testOverride = null;
            Notify?.Invoke("Test display disappeared — test cancelled");
        }

        // Right after the user asked to extend to the preferred display, go there even if auto-switch is off.
        bool restoreFocus = false;
        if (_testOverride is null && Environment.TickCount64 < _goPreferredUntil && d.PreferredAvailable)
        {
            d = d with { Target = DisplayMatcher.Match(S.Preferred, snap.Active).Display, Reason = "extended to preferred display" };
            _goPreferredUntil = 0;
            restoreFocus = _restoreFocus;
            if (_notifyExtended && d.Target is { } ext) ExtendedToPreferred?.Invoke(ext);
        }
        var target = _testOverride ?? d.Target;
        log.LogInformation("Launcher placement ({Reason}): {Target} because {Why}; preferred={Pref}",
            reason, target?.Identity.ToString() ?? "none", _testOverride is null ? d.Reason : "Test Display", d.PreferredMatch);
        if (target is not null && _window.WindowState != WindowState.Minimized && _window.IsVisible) PlaceOn(target, restoreFocus);

        if (S.RememberLastDisplay && _testOverride is null && HostDisplay is not null && S.LastDisplay != HostDisplay.Identity)
        {
            S.LastDisplay = HostDisplay.Identity;
            settings.Save();
        }
        StateChanged?.Invoke();
    }

    void PlaceOn(DisplayInfo t, bool restoreFocus)
    {
        var bounds = S.Fullscreen ? t.Bounds : Centered(t.WorkArea);
        // Moving across monitors with different DPI triggers WM_DPICHANGED, whose suggested rect WPF applies;
        // re-apply exact bounds until verified (bounded retries, D-004).
        bool ok = false;
        for (int i = 0; i < 3 && !ok; i++)
        {
            WindowPlacement.Move(_hwnd, bounds);
            ok = WindowPlacement.IsOn(_hwnd, bounds, exact: true);
        }
        HostDisplay = t;
        log.LogInformation("Placed launcher on {Display} {Bounds}: verified={Ok} actual={Actual}",
            t.Identity.FriendlyName, bounds, ok, WindowPlacement.GetBounds(_hwnd));
        // WPF may re-layout after DPI change asynchronously; verify once more after it settles.
        _window!.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (!_window.IsVisible || _window.WindowState == WindowState.Minimized || HostDisplay != t) return;
            if (!WindowPlacement.IsOn(_hwnd, bounds, exact: true))
            {
                WindowPlacement.Move(_hwnd, bounds);
                log.LogInformation("Post-layout correction: actual={Actual}", WindowPlacement.GetBounds(_hwnd));
            }
            if (restoreFocus && !SuppressActivation() && VisibleHost?.Identity == t.Identity) RestoreInputFocus?.Invoke();
        });
    }

    static PixelRect Centered(PixelRect wa)
    {
        int w = wa.Width * 3 / 4, h = wa.Height * 3 / 4;
        return new(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h);
    }

    public void BeginTest(DisplayInfo d) { _testOverride = d; Evaluate("test-begin"); }

    public void EndTest(bool confirm)
    {
        var tested = _testOverride;
        _testOverride = null;
        if (confirm && tested is not null)
        {
            S.Preferred = tested.Identity;
            settings.Save();
            log.LogInformation("Test Display confirmed via {Device}; preferred = {Id}",
                System.Windows.Input.InputManager.Current.MostRecentInputDevice?.GetType().Name, tested.Identity);
        }
        Evaluate(confirm ? "test-confirm" : "test-revert");
    }
}

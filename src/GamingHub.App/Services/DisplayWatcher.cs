using System.Windows.Interop;
using System.Windows.Threading;
using GamingHub.Core.Displays;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GamingHub.App.Services;

/// <summary>
/// Funnels Windows display notifications into one debounced <see cref="Changed"/> event (D-005),
/// with a slow fingerprint reconcile as a safety net for missed notifications.
/// </summary>
public sealed class DisplayWatcher(IDisplayProvider provider, ILogger<DisplayWatcher> log) : IDisposable
{
    const int WM_DISPLAYCHANGE = 0x7E, WM_DPICHANGED = 0x2E0, WM_DEVICECHANGE = 0x219, WM_POWERBROADCAST = 0x218;
    const int DBT_DEVNODES_CHANGED = 0x7, PBT_APMRESUMEAUTOMATIC = 0x12;

    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(750) };
    readonly DispatcherTimer _reconcile = new() { Interval = TimeSpan.FromSeconds(5) };
    HwndSource? _source;
    string _reason = "";

    public DisplaySnapshot Current { get; private set; } = new([]);
    public event Action<DisplaySnapshot, string>? Changed;

    public void Attach(IntPtr hwnd)
    {
        Current = provider.GetSnapshot();
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(Hook);
        SystemEvents.DisplaySettingsChanged += OnSystemEvent;
        SystemEvents.PowerModeChanged += OnPower;
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(_reason, force: true); };
        _reconcile.Tick += (_, _) => Refresh("reconcile", force: false);
        _reconcile.Start();
    }

    IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        switch (msg)
        {
            case WM_DISPLAYCHANGE: Schedule("WM_DISPLAYCHANGE"); break;
            case WM_DPICHANGED: Schedule("WM_DPICHANGED"); break;
            case WM_DEVICECHANGE when w == DBT_DEVNODES_CHANGED: Schedule("WM_DEVICECHANGE"); break;
            case WM_POWERBROADCAST when w == PBT_APMRESUMEAUTOMATIC: Schedule("resume"); break;
        }
        return IntPtr.Zero;
    }

    void OnSystemEvent(object? s, EventArgs e) => Dispatch(() => Schedule("DisplaySettingsChanged"));
    void OnPower(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatch(() => Schedule("PowerModeChanged.Resume"));
    }

    static void Dispatch(Action a) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(a);

    /// <summary>Restart the debounce window so bursts of notifications produce one evaluation.</summary>
    public void Schedule(string reason)
    {
        _reason = reason;
        _debounce.Stop();
        _debounce.Start();
    }

    void Refresh(string reason, bool force)
    {
        DisplaySnapshot snap;
        try { snap = provider.GetSnapshot(); }
        catch (Exception ex) { log.LogError(ex, "Display enumeration failed"); return; }
        bool changed = snap.Fingerprint != Current.Fingerprint;
        if (!changed && !force) return;
        Current = snap;
        log.LogInformation("Displays evaluated ({Reason}, changed={Changed}): {Displays}", reason, changed,
            string.Join("; ", snap.Displays.Select(d => $"{d.Identity} {d.Status} {d.Bounds} {d.Dpi}dpi primary={d.IsPrimary}")));
        Changed?.Invoke(snap, reason);
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnSystemEvent;
        SystemEvents.PowerModeChanged -= OnPower;
        _source?.RemoveHook(Hook);
        _debounce.Stop(); _reconcile.Stop();
    }
}

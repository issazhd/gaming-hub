using GamingHub.Core.Displays;
using GamingHub.Infrastructure.Audio;
using Microsoft.Extensions.Logging;

namespace GamingHub.App.Services;

/// <summary>
/// Automatic audio switching: when Gaming Hub moves to the TV (or the preferred display), sound goes to that screen's
/// own output; when it comes back to the PC monitor, the previous device is restored. Not during Test Display.
/// The device to restore is kept in settings so it survives restarts.
/// </summary>
public sealed class AudioFollowService(LauncherPlacementService placement, SettingsService settings, ILogger<AudioFollowService> log)
{
    string? _lastHost;
    int _retries;

    public event Action<string>? Notify;

    public void Start() => placement.StateChanged += () => Apply(force: false);

    void Apply(bool force)
    {
        var host = placement.HostDisplay;
        var s = settings.Current;
        if (!s.AutoAudioSwitch || host is null || placement.IsTesting) return;
        if (!force && host.Identity.DevicePath == _lastHost) return;   // only when Gaming Hub actually changed screen
        _lastHost = host.Identity.DevicePath;
        try
        {
            var outputs = AudioOutputs.List();
            var current = outputs.FirstOrDefault(o => o.IsDefault)?.Id;
            bool onTv = host.LooksLikeTv || DisplayMatcher.Match(s.Display.Preferred, [host]).Display is not null && !host.IsPrimary;
            var tvOut = AudioRouting.OutputFor(host.Identity.FriendlyName, outputs.Select(o => (o.Id, o.Name)));
            if (onTv && tvOut is null && _retries++ < 5)
            {
                // A screen's HDMI audio device appears a moment after the screen joins the desktop.
                System.Windows.Application.Current.Dispatcher.InvokeAsync(async () => { await Task.Delay(2000); Apply(force: true); });
                return;
            }
            _retries = 0;
            var d = AudioRouting.Decide(onTv, tvOut, current, s.AudioRestoreId, outputs.Select(o => o.Id).ToList());
            if (d.Remember is not null) s.AudioRestoreId = d.Remember;
            if (d.ClearRemembered) s.AudioRestoreId = null;
            if (d.SwitchTo is not null)
            {
                AudioOutputs.SetDefault(d.SwitchTo);
                var name = outputs.First(o => o.Id == d.SwitchTo).Name;
                log.LogInformation("Audio switched to {Device} (Gaming Hub on {Display})", name, host.Identity.FriendlyName);
                Notify?.Invoke($"🔊 Sound now plays on {name}");
            }
            if (d.Remember is not null || d.ClearRemembered) settings.Save();
        }
        catch (Exception ex) { log.LogWarning(ex, "Automatic audio switching failed"); }
    }
}

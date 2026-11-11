namespace GamingHub.Core.Displays;

public enum FallbackMode { WindowsPrimary, SpecificDisplay }

public sealed class LauncherDisplaySettings
{
    public DisplayIdentity? Preferred { get; set; }
    public FallbackMode FallbackMode { get; set; } = FallbackMode.WindowsPrimary;
    public DisplayIdentity? Fallback { get; set; }
    public DisplayIdentity? LastDisplay { get; set; }
    public bool AutoSwitchToPreferred { get; set; } = true;
    public bool ReturnToFallback { get; set; } = true;
    public bool AutoOpenOnPreferredConnect { get; set; } = true;
    public bool Fullscreen { get; set; } = true;
    public bool RememberLastDisplay { get; set; }
    public bool ConnectionNotifications { get; set; } = true;
    public bool CloseOnDisconnect { get; set; }
    /// <summary>When Gaming Hub extends the desktop to the preferred display, also make it the Windows main display. Off by default.</summary>
    public bool MakePreferredMainOnExtend { get; set; }
}

public sealed record LauncherDecision(DisplayInfo? Target, string Reason, MatchKind PreferredMatch)
{
    public bool PreferredAvailable => PreferredMatch is MatchKind.Exact or MatchKind.Probable;
    public bool RemapRequired => PreferredMatch == MatchKind.Ambiguous;
}

public static class LauncherDisplayPolicy
{
    /// <param name="host">Display currently hosting the launcher window, or null at startup.</param>
    public static LauncherDecision Decide(LauncherDisplaySettings s, DisplaySnapshot snap, DisplayInfo? host)
    {
        var active = snap.Active.ToList();
        var pref = DisplayMatcher.Match(s.Preferred, active);
        bool hostActive = host is not null && active.Any(d => d.Identity.DevicePath == host.Identity.DevicePath);

        if (pref.Display is not null)
        {
            if (host is null || s.AutoSwitchToPreferred || !hostActive)
                return new(pref.Display, $"preferred display ({pref.Kind})", pref.Kind);
            return new(host, "auto-switch disabled; staying", pref.Kind);
        }

        if (hostActive && !s.ReturnToFallback)
            return new(host, "preferred unavailable; return-to-fallback disabled", pref.Kind);

        if (s.RememberLastDisplay && DisplayMatcher.Match(s.LastDisplay, active).Display is { } last)
            return new(last, "last used display", pref.Kind);

        if (s.FallbackMode == FallbackMode.SpecificDisplay &&
            DisplayMatcher.Match(s.Fallback, active).Display is { } fb)
            return new(fb, "fallback display", pref.Kind);

        return new(snap.Primary, "Windows primary display", pref.Kind);
    }
}

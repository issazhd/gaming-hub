namespace GamingHub.Core.Displays;

/// <summary>What to do with the Windows default playback device after Gaming Hub moved to another screen.</summary>
public sealed record AudioDecision(string? SwitchTo, string? Remember, bool ClearRemembered);

/// <summary>
/// Automatic audio switching: on the TV (or preferred display) use that screen's own output (HDMI/DisplayPort audio is
/// named after the screen, e.g. "Beyond TV (NVIDIA High Definition Audio)"); back on the PC monitor restore the device
/// that was in use before. Pure logic — the app applies the decision.
/// </summary>
public static class AudioRouting
{
    /// <summary>The playback device that belongs to a screen: its name contains the screen's friendly name.</summary>
    public static string? OutputFor(string displayName, IEnumerable<(string Id, string Name)> outputs) =>
        displayName.Trim().Length < 2 ? null
        : outputs.FirstOrDefault(o => o.Name.Contains(displayName.Trim(), StringComparison.OrdinalIgnoreCase)).Id;

    /// <param name="onTv">Gaming Hub now shows on the TV / preferred display.</param>
    /// <param name="tvOutput">That screen's own playback device, if Windows has one.</param>
    /// <param name="currentDefault">Current Windows default playback device.</param>
    /// <param name="remembered">Device to restore later (null when nothing was switched).</param>
    /// <param name="available">Playback devices that exist right now.</param>
    public static AudioDecision Decide(bool onTv, string? tvOutput, string? currentDefault, string? remembered, IReadOnlyCollection<string> available)
    {
        if (onTv)
        {
            if (tvOutput is null || tvOutput == currentDefault) return new(null, null, false);
            return new(tvOutput, remembered ?? currentDefault, false);   // keep the first remembered device across TV↔TV moves
        }
        if (remembered is null) return new(null, null, false);
        return available.Contains(remembered) && remembered != currentDefault
            ? new(remembered, null, true)
            : new(null, null, true);   // already active, or unplugged: just forget it
    }
}

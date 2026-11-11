namespace GamingHub.Core.Displays;

public enum MatchKind { NotFound, Exact, Probable, Ambiguous }

public sealed record MatchResult(MatchKind Kind, DisplayInfo? Display, IReadOnlyList<DisplayInfo> Candidates)
{
    public static readonly MatchResult None = new(MatchKind.NotFound, null, Array.Empty<DisplayInfo>());
}

/// <summary>Finds a stored identity in the current display set without relying on display numbers.</summary>
public static class DisplayMatcher
{
    public static MatchResult Match(DisplayIdentity? wanted, IEnumerable<DisplayInfo> displays)
    {
        if (wanted is null) return MatchResult.None;
        var all = displays.ToList();

        // 1. Same EDID serial (strongest: survives port changes).
        if (wanted.HasEdid && wanted.HasEdidSerial)
        {
            var bySerial = all.Where(d => SameModel(d.Identity, wanted) && SameSerial(d.Identity, wanted)).ToList();
            if (bySerial.Count == 1) return new(MatchKind.Exact, bySerial[0], bySerial);
        }

        // 2. Same device path (stable per monitor+port), model must agree if both known.
        var byPath = all.Where(d => wanted.DevicePath is not null &&
            string.Equals(d.Identity.DevicePath, wanted.DevicePath, StringComparison.OrdinalIgnoreCase) &&
            (!wanted.HasEdid || !d.Identity.HasEdid || SameModel(d.Identity, wanted))).ToList();
        if (byPath.Count == 1) return new(MatchKind.Exact, byPath[0], byPath);

        // 3. Same model (moved port / serial missing). Unique → probable, several → ambiguous.
        if (!wanted.HasEdid) return MatchResult.None;
        var byModel = all.Where(d => SameModel(d.Identity, wanted) &&
            (!wanted.HasEdidSerial || !d.Identity.HasEdidSerial || SameSerial(d.Identity, wanted))).ToList();
        if (byModel.Count == 1) return new(MatchKind.Probable, byModel[0], byModel);
        if (byModel.Count > 1)
        {
            // Tie-break on connector instance; otherwise the user must remap.
            var byConn = byModel.Where(d => d.Identity.ConnectorInstance == wanted.ConnectorInstance).ToList();
            return byConn.Count == 1
                ? new(MatchKind.Probable, byConn[0], byModel)
                : new(MatchKind.Ambiguous, null, byModel);
        }
        return MatchResult.None;
    }

    static bool SameModel(DisplayIdentity a, DisplayIdentity b) =>
        a.HasEdid && b.HasEdid && a.EdidManufacturer == b.EdidManufacturer && a.EdidProductCode == b.EdidProductCode;

    static bool SameSerial(DisplayIdentity a, DisplayIdentity b) =>
        (a.EdidSerialNumber != 0 && a.EdidSerialNumber == b.EdidSerialNumber) ||
        (!string.IsNullOrWhiteSpace(a.EdidSerialString) && a.EdidSerialString == b.EdidSerialString);
}

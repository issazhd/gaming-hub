namespace GamingHub.Core.Displays;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int IntersectionArea(PixelRect o)
    {
        int w = Math.Min(Right, o.Right) - Math.Max(X, o.X);
        int h = Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }
}

/// <summary>Persistable identity of a physical display. GDI name is a hint only.</summary>
public sealed record DisplayIdentity
{
    public string? DevicePath { get; init; }
    public string? EdidManufacturer { get; init; }
    public ushort EdidProductCode { get; init; }
    public uint EdidSerialNumber { get; init; }
    public string? EdidSerialString { get; init; }
    public string FriendlyName { get; init; } = "";
    public uint ConnectorInstance { get; init; }
    public string? LastGdiName { get; init; }

    public bool HasEdid => !string.IsNullOrEmpty(EdidManufacturer) && EdidProductCode != 0;
    public bool HasEdidSerial => EdidSerialNumber != 0 || !string.IsNullOrWhiteSpace(EdidSerialString);
    public string HardwareId => HasEdid ? $"{EdidManufacturer}{EdidProductCode:X4}" : "";

    public override string ToString() =>
        $"{FriendlyName} [{HardwareId}{(HasEdidSerial ? " SN " + (EdidSerialString ?? EdidSerialNumber.ToString()) : "")}]";
}

public enum DisplayStatus { Active, ConnectedInactive }

/// <summary>A display as currently seen by Windows.</summary>
public sealed record DisplayInfo
{
    public required DisplayIdentity Identity { get; init; }
    public string? Manufacturer { get; init; }
    public DisplayStatus Status { get; init; }
    public bool IsPrimary { get; init; }
    public PixelRect Bounds { get; init; }
    public PixelRect WorkArea { get; init; }
    public double RefreshHz { get; init; }
    public uint Dpi { get; init; } = 96;
    public string OutputTechnology { get; init; } = "";
    public string? GdiName => Identity.LastGdiName;
    public bool IsActive => Status == DisplayStatus.Active;
    public string Resolution => IsActive ? $"{Bounds.Width}×{Bounds.Height} @ {RefreshHz:0.##} Hz" : "—";
    public int ScalePercent => (int)Math.Round(Dpi / 96.0 * 100);

    static readonly string[] TvBrands = ["TCL", "Hisense", "VIZIO", "Vizio", "Sony", "Philips TV", "Panasonic", "Sharp", "Toshiba", "Skyworth"];

    /// <summary>Best-effort: Windows doesn't report "TV". Name says TV, a TV-only brand, or a 4K+ HDMI panel.</summary>
    public bool LooksLikeTv =>
        Identity.FriendlyName.Contains("TV", StringComparison.OrdinalIgnoreCase)
        || (Manufacturer is { } m && TvBrands.Any(b => m.StartsWith(b, StringComparison.OrdinalIgnoreCase)))
        || (OutputTechnology.StartsWith("HDMI", StringComparison.OrdinalIgnoreCase) && Bounds.Width >= 3840);
}

public sealed record DisplaySnapshot(IReadOnlyList<DisplayInfo> Displays)
{
    public IEnumerable<DisplayInfo> Active => Displays.Where(d => d.IsActive);
    public DisplayInfo? Primary => Active.FirstOrDefault(d => d.IsPrimary) ?? Active.FirstOrDefault();

    /// <summary>Cheap string that changes when anything placement-relevant changes.</summary>
    public string Fingerprint => string.Join("|", Displays
        .OrderBy(d => d.Identity.DevicePath)
        .Select(d => $"{d.Identity.DevicePath}:{d.Status}:{d.IsPrimary}:{d.Bounds}:{d.Dpi}:{d.RefreshHz:0}"));
}

public interface IDisplayProvider
{
    DisplaySnapshot GetSnapshot();
}

using System.Runtime.InteropServices;
using GamingHub.Core.Displays;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using static GamingHub.Infrastructure.Displays.NativeDisplay;

namespace GamingHub.Infrastructure.Displays;

/// <summary>Enumerates displays via QueryDisplayConfig (identity) joined with EnumDisplayMonitors (geometry/DPI).</summary>
public sealed class WindowsDisplayProvider(ILogger<WindowsDisplayProvider> log) : IDisplayProvider
{
    readonly Dictionary<string, Edid.EdidInfo?> _edidCache = new(StringComparer.OrdinalIgnoreCase);

    public DisplaySnapshot GetSnapshot()
    {
        var monitors = GetMonitors();
        var result = new Dictionary<string, DisplayInfo>();

        foreach (var p in QueryPaths(QDC_ALL_PATHS))
        {
            if (p.target.targetAvailable == 0) continue;
            bool active = (p.flags & PATH_ACTIVE) != 0;
            var key = $"{p.target.adapterId.Low}:{p.target.adapterId.High}:{p.target.id}";
            // ALL_PATHS yields one path per source×target; keep the active one if present.
            if (result.TryGetValue(key, out var existing) && (existing.IsActive || !active)) continue;

            var t = GetTargetName(p.target.adapterId, p.target.id);
            if (t is null) continue;
            var tn = t.Value;
            string? gdi = active ? GetSourceName(p.source.adapterId, p.source.id) : null;
            var edid = ReadEdid(tn.monitorDevicePath);
            bool idsValid = (tn.flags & 0x4) != 0;
            var mfr = edid?.Manufacturer ?? (idsValid ? Edid.DecodePnpId(tn.edidManufactureId) : null);

            var identity = new DisplayIdentity
            {
                DevicePath = string.IsNullOrEmpty(tn.monitorDevicePath) ? null : tn.monitorDevicePath,
                EdidManufacturer = mfr,
                EdidProductCode = edid?.ProductCode ?? (idsValid ? tn.edidProductCodeId : (ushort)0),
                EdidSerialNumber = edid?.SerialNumber ?? 0,
                EdidSerialString = edid?.SerialString,
                FriendlyName = FirstNonEmpty(tn.monitorFriendlyDeviceName, edid?.Name, "Unknown display"),
                ConnectorInstance = tn.connectorInstance,
                LastGdiName = gdi,
            };

            monitors.TryGetValue(gdi ?? "", out var m);
            var rr = p.target.refreshRate;
            result[key] = new DisplayInfo
            {
                Identity = identity,
                Manufacturer = Edid.VendorName(mfr),
                Status = active && m is not null ? DisplayStatus.Active : DisplayStatus.ConnectedInactive,
                IsPrimary = m?.Primary ?? false,
                Bounds = m?.Bounds ?? default,
                WorkArea = m?.Work ?? default,
                Dpi = m?.Dpi ?? 96,
                RefreshHz = active && rr.Den != 0 ? Math.Round((double)rr.Num / rr.Den, 2) : 0,
                OutputTechnology = OutputTech(tn.outputTechnology),
            };
        }
        return new DisplaySnapshot(result.Values.OrderByDescending(d => d.IsActive).ThenBy(d => d.Bounds.X).ToList());
    }

    static string FirstNonEmpty(params string?[] s) => s.First(x => !string.IsNullOrWhiteSpace(x))!;

    PATH_INFO[] QueryPaths(uint flags)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (GetDisplayConfigBufferSizes(flags, out var np, out var nm) != 0) break;
            var paths = new PATH_INFO[np];
            var modes = new MODE_INFO[nm];
            int rc = QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (rc == 0) return paths.Take((int)np).ToArray();
            if (rc != ERROR_INSUFFICIENT_BUFFER) { log.LogWarning("QueryDisplayConfig failed: {Rc}", rc); break; }
        }
        return [];
    }

    static TARGET_DEVICE_NAME? GetTargetName(LUID adapter, uint id)
    {
        var r = new TARGET_DEVICE_NAME();
        r.header = new() { type = GET_TARGET_NAME, size = (uint)Marshal.SizeOf<TARGET_DEVICE_NAME>(), adapterId = adapter, id = id };
        return DisplayConfigGetDeviceInfo(ref r) == 0 ? r : null;
    }

    static string? GetSourceName(LUID adapter, uint id)
    {
        var r = new SOURCE_DEVICE_NAME();
        r.header = new() { type = GET_SOURCE_NAME, size = (uint)Marshal.SizeOf<SOURCE_DEVICE_NAME>(), adapterId = adapter, id = id };
        return DisplayConfigGetDeviceInfo(ref r) == 0 ? r.viewGdiDeviceName : null;
    }

    /// <summary>\\?\DISPLAY#AOC2590#5&amp;x&amp;0&amp;UID1#{guid} → HKLM\...\Enum\DISPLAY\AOC2590\5&amp;x&amp;0&amp;UID1\Device Parameters\EDID</summary>
    Edid.EdidInfo? ReadEdid(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return null;
        if (_edidCache.TryGetValue(devicePath, out var cached)) return cached;
        Edid.EdidInfo? info = null;
        try
        {
            var parts = devicePath.Split('#');
            if (parts.Length >= 3)
            {
                using var k = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
                info = Edid.Parse(k?.GetValue("EDID") as byte[]);
            }
        }
        catch (Exception ex) { log.LogDebug(ex, "EDID read failed for {Path}", devicePath); }
        return _edidCache[devicePath] = info;
    }

    sealed record Mon(PixelRect Bounds, PixelRect Work, bool Primary, uint Dpi);

    static Dictionary<string, Mon> GetMonitors()
    {
        var map = new Dictionary<string, Mon>(StringComparer.OrdinalIgnoreCase);
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi))
            {
                uint dpi = GetDpiForMonitor(h, 0, out var dx, out _) == 0 ? dx : 96;
                map[mi.szDevice] = new(ToRect(mi.rcMonitor), ToRect(mi.rcWork), (mi.dwFlags & 1) != 0, dpi);
            }
            return true;
        }, IntPtr.Zero);
        return map;
    }

    static PixelRect ToRect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    static string OutputTech(uint t) => t switch
    {
        0 => "VGA", 4 => "DVI", 5 => "HDMI", 10 => "DisplayPort", 11 => "DisplayPort (embedded)",
        15 => "Miracast", 16 => "Indirect (wired)", 17 => "Indirect (virtual)", 0x80000000 => "Internal",
        _ => $"Other ({t})",
    };
}

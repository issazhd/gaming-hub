using System.Runtime.InteropServices;

namespace GamingHub.Infrastructure.Displays;

/// <summary>Windows display topology changes — only ever on explicit user request.</summary>
public static class DisplayTopology
{
    const uint SDC_TOPOLOGY_EXTEND = 0x4, SDC_APPLY = 0x80;

    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);

    /// <summary>Same as Win+P › Extend: every connected display becomes part of the desktop. Returns the Win32 result (0 = OK).</summary>
    public static int Extend() => SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_TOPOLOGY_EXTEND | SDC_APPLY);

    public static string? StopExtending(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return "This display could not be identified.";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int rc = NativeDisplay.GetDisplayConfigBufferSizes(NativeDisplay.QDC_ONLY_ACTIVE_PATHS, out var np, out var nm);
            if (rc != 0) return $"Windows could not read the active displays (error {rc}).";
            var paths = new NativeDisplay.PATH_INFO[np];
            var modes = new NativeDisplay.MODE_INFO[nm];
            rc = NativeDisplay.QueryDisplayConfig(NativeDisplay.QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (rc == NativeDisplay.ERROR_INSUFFICIENT_BUFFER) continue;
            if (rc != 0) return $"Windows could not read the active displays (error {rc}).";
            var active = paths.Take((int)np).ToArray();
            var remaining = active.Where(p => !string.Equals(devicePath,
                WindowsDisplayProvider.GetTargetName(p.target.adapterId, p.target.id)?.monitorDevicePath,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (remaining.Length != active.Length - 1) return "This display is no longer uniquely active.";
            if (remaining.Length == 0) return "Keep at least one display active.";
            var kept = new List<NativeDisplay.MODE_INFO>();
            var indices = new Dictionary<uint, uint>();
            uint Remap(uint index)
            {
                if (indices.TryGetValue(index, out var mapped)) return mapped;
                indices[index] = mapped = (uint)kept.Count;
                kept.Add(modes[index]);
                return mapped;
            }
            for (int i = 0; i < remaining.Length; i++)
            {
                ref var p = ref remaining[i];
                if (p.source.modeInfoIdx >= nm || p.target.modeInfoIdx >= nm) return "Windows did not provide a complete display layout.";
                p.source.modeInfoIdx = Remap(p.source.modeInfoIdx);
                p.target.modeInfoIdx = Remap(p.target.modeInfoIdx);
            }
            var layout = kept.ToArray();
            var anchor = layout.Where(m => m.infoType == NativeDisplay.MODE_INFO_TYPE_SOURCE)
                .OrderBy(m => (long)m.sourceX * m.sourceX + (long)m.sourceY * m.sourceY).First();
            for (int i = 0; i < layout.Length; i++)
                if (layout[i].infoType == NativeDisplay.MODE_INFO_TYPE_SOURCE)
                { layout[i].sourceX -= anchor.sourceX; layout[i].sourceY -= anchor.sourceY; }
            uint supplied = NativeDisplay.SDC_USE_SUPPLIED_DISPLAY_CONFIG;
            rc = NativeDisplay.SetDisplayConfig((uint)remaining.Length, remaining, (uint)layout.Length, layout, supplied | 0x40); // SDC_VALIDATE
            if (rc == 0) rc = NativeDisplay.SetDisplayConfig((uint)remaining.Length, remaining, (uint)layout.Length, layout,
                supplied | NativeDisplay.SDC_APPLY | NativeDisplay.SDC_SAVE_TO_DATABASE);
            return rc == 0 ? null : $"Windows could not stop extending to this display (error {rc}).";
        }
        return "The display layout is changing. Try again.";
    }

    // ---- Main (primary) display: the classic ChangeDisplaySettingsEx method — the main display sits at (0,0),
    // so every active display is shifted by the new main display's position, then all changes apply at once. ----
    const int ENUM_CURRENT_SETTINGS = -1, DM_POSITION = 0x20, DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    const uint CDS_UPDATEREGISTRY = 0x1, CDS_SET_PRIMARY = 0x10, CDS_NORESET = 0x10000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent,
            dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string? device, uint i, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string? device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string? device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr param);

    /// <summary>Makes the display with this GDI name (e.g. \\.\DISPLAY2) the Windows main display, keeping the layout.
    /// Returns null on success, else a message. Resolution, refresh rate and HDR are not touched.</summary>
    public static string? SetMain(string gdiName)
    {
        var modern = SetMainDisplayConfig(gdiName);   // what Windows Settings uses; works with drivers that refuse the legacy call
        return modern is null ? null : SetMainLegacy(gdiName) is null ? null : modern;
    }

    /// <summary>QueryDisplayConfig → move every source so the chosen one is at (0,0) → SetDisplayConfig (saved, like Settings).</summary>
    static string? SetMainDisplayConfig(string gdiName)
    {
        uint np = 0, nm = 0;
        NativeDisplay.PATH_INFO[] paths = []; NativeDisplay.MODE_INFO[] modes = [];
        int rc = -1;
        for (int attempt = 0; attempt < 3 && rc != 0; attempt++)
        {
            if (NativeDisplay.GetDisplayConfigBufferSizes(NativeDisplay.QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) break;
            paths = new NativeDisplay.PATH_INFO[np]; modes = new NativeDisplay.MODE_INFO[nm];
            rc = NativeDisplay.QueryDisplayConfig(NativeDisplay.QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
        }
        if (rc != 0) return $"Windows couldn't read the display layout (error {rc})";

        int origin = -1;
        for (int i = 0; i < np && origin < 0; i++)
        {
            var src = paths[i].source;
            if (src.modeInfoIdx < nm && string.Equals(SourceName(src.adapterId, src.id), gdiName, StringComparison.OrdinalIgnoreCase))
                origin = (int)src.modeInfoIdx;
        }
        if (origin < 0) return $"{gdiName} isn't part of the desktop";
        int ox = modes[origin].sourceX, oy = modes[origin].sourceY;
        if (ox == 0 && oy == 0) return null;   // already the main display
        for (int i = 0; i < nm; i++)
            if (modes[i].infoType == NativeDisplay.MODE_INFO_TYPE_SOURCE) { modes[i].sourceX -= ox; modes[i].sourceY -= oy; }

        rc = NativeDisplay.SetDisplayConfig(np, paths, nm, modes,
            NativeDisplay.SDC_APPLY | NativeDisplay.SDC_USE_SUPPLIED_DISPLAY_CONFIG | NativeDisplay.SDC_SAVE_TO_DATABASE | NativeDisplay.SDC_ALLOW_CHANGES);
        return rc == 0 ? null : $"Windows refused the new layout (error {rc})";
    }

    static string? SourceName(NativeDisplay.LUID adapter, uint id)
    {
        var r = new NativeDisplay.SOURCE_DEVICE_NAME();
        r.header = new() { type = NativeDisplay.GET_SOURCE_NAME, size = (uint)Marshal.SizeOf<NativeDisplay.SOURCE_DEVICE_NAME>(), adapterId = adapter, id = id };
        return NativeDisplay.DisplayConfigGetDeviceInfo(ref r) == 0 ? r.viewGdiDeviceName : null;
    }

    static string? SetMainLegacy(string gdiName)
    {
        var active = new List<DEVMODE>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>())
        {
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm)) { dm.dmDeviceName = dd.DeviceName; active.Add(dm); }
        }
        var main = active.FindIndex(d => string.Equals(d.dmDeviceName, gdiName, StringComparison.OrdinalIgnoreCase));
        if (main < 0) return $"{gdiName} isn't part of the desktop";
        int ox = active[main].dmPositionX, oy = active[main].dmPositionY;
        if (ox == 0 && oy == 0) return null;   // already the main display
        for (int i = 0; i < active.Count; i++)
        {
            var dm = active[i];
            dm.dmPositionX -= ox; dm.dmPositionY -= oy;
            dm.dmFields = DM_POSITION;
            int rc = ChangeDisplaySettingsEx(dm.dmDeviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET | (i == main ? CDS_SET_PRIMARY : 0), IntPtr.Zero);
            if (rc != 0) return $"Windows refused the change for {dm.dmDeviceName} (error {rc})";
        }
        int apply = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        return apply == 0 ? null : $"Windows couldn't apply the change (error {apply})";
    }
}

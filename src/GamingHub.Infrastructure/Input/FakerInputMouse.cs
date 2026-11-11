using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GamingHub.Infrastructure.Input;

/// <summary>
/// Pointer output through the FakerInput driver (MIT, Travis Nickles — installed by the user, never by Gaming Hub).
/// FakerInput is a virtual HID mouse/keyboard, so Windows treats its input like a real mouse: unlike SendInput it
/// reaches UAC prompts and administrator windows. Protocol from FakerInputDll (fakerinputclient.cpp): open the HID
/// collection VID FE0F / PID 00FF, usage page FF00, usage 1, and write 65-byte control reports that wrap a mouse report.
/// </summary>
public sealed class FakerInputMouse : IDisposable
{
    const ushort Vid = 0xFE0F, Pid = 0x00FF, UsagePage = 0xFF00, UsageControl = 0x0001;
    const byte ReportControl = 0x40, ReportRelativeMouse = 0x03;
    const int ControlReportSize = 0x41;

    SafeFileHandle? _h;
    long _retryAt;
    byte _buttons;

    public bool IsConnected => _h is { IsInvalid: false, IsClosed: false };

    /// <summary>Connects if the driver is installed (checks again at most every 10 s while it isn't).</summary>
    public bool TryConnect()
    {
        if (IsConnected) return true;
        if (Environment.TickCount64 < _retryAt) return false;
        _retryAt = Environment.TickCount64 + 10_000;
        _h = Find();
        return IsConnected;
    }

    /// <summary>Relative movement in mickeys (Windows pointer speed/acceleration apply, as for a real mouse).</summary>
    public bool Move(int dx, int dy) => Send(_buttons, dx, dy);

    /// <summary>Left (1), right (2) or middle (4) button down/up.</summary>
    public bool Button(byte button, bool down)
    {
        _buttons = (byte)(down ? _buttons | button : _buttons & ~button);
        return Send(_buttons, 0, 0);
    }

    public bool Click(bool right = false) => Button(right ? (byte)2 : (byte)1, true) && Button(right ? (byte)2 : (byte)1, false);

    bool Send(byte buttons, int dx, int dy)
    {
        if (!TryConnect()) return false;
        var r = new byte[ControlReportSize];
        r[0] = ReportControl; r[1] = 8;                   // header: control report wrapping an 8-byte mouse report
        r[2] = ReportRelativeMouse; r[3] = buttons;
        BitConverter.TryWriteBytes(r.AsSpan(4), (short)Math.Clamp(dx, -32767, 32767));
        BitConverter.TryWriteBytes(r.AsSpan(6), (short)Math.Clamp(dy, -32767, 32767));
        // r[8] wheel, r[9] horizontal wheel = 0
        if (WriteFile(_h!, r, r.Length, out _, IntPtr.Zero)) return true;
        _h!.Dispose(); _h = null;                          // driver removed / device reset: reconnect later
        return false;
    }

    public void Dispose() => _h?.Dispose();

    /// <summary>True when the FakerInput driver's device is present.</summary>
    public static bool IsInstalled() { using var h = Find(); return h is not null; }

    // ---- HID device search (SetupAPI + hid.dll) ----
    static SafeFileHandle? Find()
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x2 | 0x10);   // PRESENT | DEVICEINTERFACE
        if (set == new IntPtr(-1)) return null;
        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref iface); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size <= 0) continue;
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);   // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, buf, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(buf + 4);
                    if (path is null) continue;
                    using (var probe = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))   // attributes only: no access needed
                        if (probe.IsInvalid || !IsControlCollection(probe)) continue;
                    var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);   // read/write, share read/write, open existing
                    if (!h.IsInvalid) return h;
                    h.Dispose();
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return null;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    static bool IsControlCollection(SafeFileHandle h)
    {
        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(h, ref attr) || attr.VendorID != Vid || attr.ProductID != Pid) return false;
        if (!HidD_GetPreparsedData(h, out var ppd)) return false;
        try { return HidP_GetCaps(ppd, out var caps) == 0x110000 && caps.UsagePage == UsagePage && caps.Usage == UsageControl; }   // HIDP_STATUS_SUCCESS
        finally { HidD_FreePreparsedData(ppd); }
    }

    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }
    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr ppd);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr ppd);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr ppd, out HIDP_CAPS caps);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, out int written, IntPtr overlapped);
}

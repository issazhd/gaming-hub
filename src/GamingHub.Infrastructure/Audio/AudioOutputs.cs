using System.Runtime.InteropServices;

namespace GamingHub.Infrastructure.Audio;

public sealed record AudioOutput(string Id, string Name, bool IsDefault)
{
    public override string ToString() => Name;   // shown by the Settings dropdown
}

/// <summary>
/// Lists active playback devices (Core Audio MMDevice API) and makes one the Windows default for all roles,
/// the same way Windows' own Sound settings and tools like EarTrumpet/SoundSwitch do (IPolicyConfig).
/// Only changed when the user picks a device; nothing is switched automatically.
/// </summary>
public static class AudioOutputs
{
    const int eRender = 0, DEVICE_STATE_ACTIVE = 1, eConsole = 0, eMultimedia = 1, eCommunications = 2;
    static PROPERTYKEY FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public static string? DefaultName()
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            if (en.GetDefaultAudioEndpoint(eRender, eMultimedia, out var dev) != 0) return null;
            try { return Name(dev); }
            finally { Marshal.ReleaseComObject(dev); }
        }
        finally { Marshal.ReleaseComObject(en); }
    }

    public static IReadOnlyList<AudioOutput> List()
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            string? defaultId = null;
            if (en.GetDefaultAudioEndpoint(eRender, eMultimedia, out var def) == 0) { def.GetId(out defaultId); Marshal.ReleaseComObject(def); }
            Marshal.ThrowExceptionForHR(en.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var col));
            col.GetCount(out int n);
            var list = new List<AudioOutput>(n);
            for (int i = 0; i < n; i++)
            {
                col.Item(i, out var dev);
                dev.GetId(out var id);
                list.Add(new AudioOutput(id, Name(dev) ?? id, id == defaultId));
                Marshal.ReleaseComObject(dev);
            }
            Marshal.ReleaseComObject(col);
            return list.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally { Marshal.ReleaseComObject(en); }
    }

    /// <summary>Master volume of the default playback device: 0–100 and mute (Core Audio IAudioEndpointVolume).</summary>
    public static (int Percent, bool Muted) GetVolume() => WithVolume(v =>
    {
        v.GetMasterVolumeLevelScalar(out float f);
        v.GetMute(out bool m);
        return ((int)Math.Round(f * 100), m);
    });

    public static void SetVolume(int percent) => WithVolume(v =>
    {
        var ctx = Guid.Empty;
        v.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref ctx);
        if (percent > 0) v.SetMute(false, ref ctx);
        return 0;
    });

    public static void SetMute(bool mute) => WithVolume(v => { var ctx = Guid.Empty; v.SetMute(mute, ref ctx); return 0; });

    static T WithVolume<T>(Func<IAudioEndpointVolume, T> use)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(eRender, eMultimedia, out var dev));
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var ptr));
                var vol = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(ptr);
                Marshal.Release(ptr);
                try { return use(vol); } finally { Marshal.ReleaseComObject(vol); }
            }
            finally { Marshal.ReleaseComObject(dev); }
        }
        finally { Marshal.ReleaseComObject(en); }
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    public static void SetDefault(string id)
    {
        var policy = (IPolicyConfig)new CPolicyConfigClient();
        try
        {
            foreach (var role in new[] { eConsole, eMultimedia, eCommunications })
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
        }
        finally { Marshal.ReleaseComObject(policy); }
    }

    static string? Name(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0, out var store) != 0) return null;
        try
        {
            if (store.GetValue(ref FriendlyName, out var v) != 0) return null;
            try { return v.vt == 31 ? Marshal.PtrToStringUni(v.ptr) : null; }   // VT_LPWSTR
            finally { PropVariantClear(ref v); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY(Guid fmtid, int pid) { public Guid fmtid = fmtid; public int pid = pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr ptr; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom;
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class CPolicyConfigClient;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    /// <summary>Undocumented but long-stable (Windows 7–11) interface behind the Sound control panel.</summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }
}

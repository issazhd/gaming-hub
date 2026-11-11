using System.Runtime.InteropServices;

namespace GamingHub.App.Services;

/// <summary>
/// Soft, original UI sounds synthesized at startup (short sine "ticks" with a fast decay) — no copied console sounds.
/// Played asynchronously through the default playback device; a newer sound simply replaces the previous one.
/// </summary>
public static class UiSounds
{
    public enum Kind { Move, Select, Back }

    const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;
    [DllImport("winmm.dll")] static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    // Each sound lives in two permanent unmanaged copies, used alternately: Windows reads async sounds after the call
    // returns, and restarting the very same buffer back-to-back made every second tick silent.
    static readonly Dictionary<Kind, IntPtr[]> Players = new()
    {
        [Kind.Move] = Make((1568, 0), 0.045, 0.16),                 // light tick (G6)
        [Kind.Select] = Make((1318, 1976), 0.12, 0.20),             // two-note rise (E6 → B6)
        [Kind.Back] = Make((1175, 880), 0.10, 0.16),                // two-note fall (D6 → A5)
    };

    public static bool Enabled { get; set; } = true;
    static int _flip;

    public static void Play(Kind k)
    {
        if (!Enabled) return;
        var copies = Players[k];
        PlaySound(copies[_flip ^= 1], IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT);
    }

    /// <summary>16-bit mono WAV: one tone, or two tones back to back, each with a click-free attack and exponential decay.</summary>
    static IntPtr[] Make((double F1, double F2) f, double seconds, double volume)
    {
        const int Rate = 44100;
        int n = (int)(Rate * seconds);
        int lead = Rate / 20;   // 50 ms of silence first: HDMI/DisplayPort audio often clips the start of a sound after idle
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + (lead + n) * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write((lead + n) * 2);
        for (int i = 0; i < lead; i++) w.Write((short)0);
        int half = f.F2 > 0 ? n / 2 : n;
        for (int i = 0; i < n; i++)
        {
            bool second = f.F2 > 0 && i >= half;
            int j = second ? i - half : i;
            int len = second ? n - half : half;
            double t = (double)j / Rate;
            double env = Math.Min(1, j / (Rate * 0.002)) * Math.Exp(-5.0 * j / len);   // 2 ms attack, smooth decay
            double s = Math.Sin(2 * Math.PI * (second ? f.F2 : f.F1) * t) * 0.85 + Math.Sin(4 * Math.PI * (second ? f.F2 : f.F1) * t) * 0.15;
            w.Write((short)(s * env * volume * short.MaxValue));
        }
        var bytes = ms.ToArray();
        var copies = new IntPtr[2];
        for (int c = 0; c < 2; c++) { copies[c] = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, copies[c], bytes.Length); }
        return copies;   // kept for the app's lifetime (a few KB)
    }
}

using System.Text;

namespace GamingHub.Core.Displays;

public static class Edid
{
    static readonly Dictionary<string, string> Vendors = new()
    {
        ["AOC"] = "AOC", ["TCL"] = "TCL", ["SAM"] = "Samsung", ["GSM"] = "LG", ["DEL"] = "Dell",
        ["ACR"] = "Acer", ["AUS"] = "ASUS", ["BNQ"] = "BenQ", ["HWP"] = "HP", ["LEN"] = "Lenovo",
        ["SNY"] = "Sony", ["PHL"] = "Philips", ["MSI"] = "MSI", ["VSC"] = "ViewSonic", ["GBT"] = "Gigabyte",
        ["HSN"] = "Hisense", ["VIZ"] = "Vizio", ["PNP"] = "Generic", ["MEI"] = "Panasonic", ["SHP"] = "Sharp",
    };

    public static string? VendorName(string? pnp) =>
        pnp is null ? null : Vendors.TryGetValue(pnp, out var n) ? n : pnp;

    /// <summary>Decodes a 3-letter PNP ID from the big-endian packed EDID value.</summary>
    public static string DecodePnpId(ushort bigEndian)
    {
        int v = (bigEndian >> 8) | ((bigEndian & 0xFF) << 8);
        return DecodePnpIdNative((ushort)v);
    }

    public static string DecodePnpIdNative(ushort v) => new(new[]
    {
        (char)('A' - 1 + ((v >> 10) & 0x1F)),
        (char)('A' - 1 + ((v >> 5) & 0x1F)),
        (char)('A' - 1 + (v & 0x1F)),
    });

    public sealed record EdidInfo(string Manufacturer, ushort ProductCode, uint SerialNumber, string? SerialString, string? Name);

    public static EdidInfo? Parse(byte[]? e)
    {
        if (e is null || e.Length < 128 || e[0] != 0 || e[1] != 0xFF || e[7] != 0) return null;
        var mfr = DecodePnpIdNative((ushort)((e[8] << 8) | e[9]));
        var product = (ushort)(e[10] | (e[11] << 8));
        var serial = BitConverter.ToUInt32(e, 12);
        string? sn = null, name = null;
        for (int o = 54; o <= 108; o += 18)
        {
            if (e[o] != 0 || e[o + 1] != 0) continue; // detailed timing, not a descriptor
            var text = Encoding.ASCII.GetString(e, o + 5, 13).Split('\n')[0].Trim();
            if (e[o + 3] == 0xFF) sn = text;
            else if (e[o + 3] == 0xFC) name = text;
        }
        return new(mfr, product, serial, string.IsNullOrEmpty(sn) ? null : sn, name);
    }
}

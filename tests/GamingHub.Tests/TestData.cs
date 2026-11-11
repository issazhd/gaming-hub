using GamingHub.Core.Displays;

namespace GamingHub.Tests;

static class TestData
{
    public static DisplayInfo Disp(string path, string mfr, ushort product, uint serial = 0, bool primary = false,
        int x = 0, int w = 1920, int h = 1080, bool active = true, uint conn = 0, uint dpi = 96, string? gdi = null) => new()
    {
        Identity = new DisplayIdentity
        {
            DevicePath = path, EdidManufacturer = mfr, EdidProductCode = product, EdidSerialNumber = serial,
            FriendlyName = $"{mfr} {product:X4}", ConnectorInstance = conn, LastGdiName = gdi,
        },
        Status = active ? DisplayStatus.Active : DisplayStatus.ConnectedInactive,
        IsPrimary = primary, Bounds = new PixelRect(x, 0, w, h), Dpi = dpi, RefreshHz = 60,
    };

    public static readonly DisplayInfo Aoc = Disp(@"\\?\DISPLAY#AOC2590#5&1&0&UID41217#{g}", "AOC", 0x2590, primary: true, gdi: @"\\.\DISPLAY1");
    public static readonly DisplayInfo Tv = Disp(@"\\?\DISPLAY#TCL9653#5&1&0&UID41216#{g}", "TCL", 0x9653, x: -3840, w: 3840, h: 2160, dpi: 288, gdi: @"\\.\DISPLAY2");
    public static readonly DisplayInfo Third = Disp(@"\\?\DISPLAY#DEL4100#5&1&0&UID3#{g}", "DEL", 0x4100, serial: 77, x: 1920, w: 2560, h: 1440, dpi: 120);

    public static DisplaySnapshot Snap(params DisplayInfo[] d) => new(d);
}

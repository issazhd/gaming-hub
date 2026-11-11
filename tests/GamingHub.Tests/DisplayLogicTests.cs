using GamingHub.Core.Displays;
using static GamingHub.Tests.TestData;

namespace GamingHub.Tests;

public class EdidTests
{
    [Fact]
    public void DecodesPnpIdFromBigEndianDisplayConfigValue()
    {
        // "AOC" = 0x05E3 native; DisplayConfig delivers it byte-swapped.
        Assert.Equal("AOC", Edid.DecodePnpIdNative(0x05E3));
        Assert.Equal("AOC", Edid.DecodePnpId(0xE305));
    }

    [Fact]
    public void ParsesSyntheticEdid()
    {
        var e = new byte[128];
        new byte[] { 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0 }.CopyTo(e, 0);
        e[8] = 0x05; e[9] = 0xE3;          // AOC
        e[10] = 0x90; e[11] = 0x25;        // product 0x2590
        BitConverter.GetBytes(12345u).CopyTo(e, 12);
        e[57] = 0xFF; "ABC123\n     "u8.ToArray().CopyTo(e, 59);   // serial descriptor @54
        e[75] = 0xFC; "2590G4\n     "u8.ToArray().CopyTo(e, 77);   // name descriptor @72
        var info = Edid.Parse(e)!;
        Assert.Equal(("AOC", (ushort)0x2590, 12345u, "ABC123", "2590G4"),
            (info.Manufacturer, info.ProductCode, info.SerialNumber, info.SerialString, info.Name));
    }

    [Fact] public void RejectsInvalidEdid() => Assert.Null(Edid.Parse(new byte[128]));
}

public class DisplayMatcherTests
{
    [Fact]
    public void MatchesByDevicePathDespiteGdiRenumbering()
    {
        var renumbered = Aoc with { Identity = Aoc.Identity with { LastGdiName = @"\\.\DISPLAY3" } };
        var r = DisplayMatcher.Match(Aoc.Identity, [Tv, renumbered]);
        Assert.Equal(MatchKind.Exact, r.Kind);
        Assert.Same(renumbered, r.Display);
    }

    [Fact]
    public void SerialMatchSurvivesPortChange()
    {
        var moved = Third with { Identity = Third.Identity with { DevicePath = "other-port" } };
        Assert.Equal(MatchKind.Exact, DisplayMatcher.Match(Third.Identity, [Aoc, moved]).Kind);
    }

    [Fact]
    public void UniqueModelOnNewPortIsProbable()
    {
        var moved = Tv with { Identity = Tv.Identity with { DevicePath = "new" } };
        var r = DisplayMatcher.Match(Tv.Identity, [Aoc, moved]);
        Assert.Equal(MatchKind.Probable, r.Kind);
    }

    [Fact]
    public void TwoIdenticalSerialLessMonitorsOnNewPortsAreAmbiguous()
    {
        var a = Disp("p1", "AOC", 0x2590, conn: 1);
        var b = Disp("p2", "AOC", 0x2590, conn: 2);
        var wanted = Aoc.Identity with { DevicePath = "gone", ConnectorInstance = 9 };
        var r = DisplayMatcher.Match(wanted, [a, b]);
        Assert.Equal(MatchKind.Ambiguous, r.Kind);
        Assert.Equal(2, r.Candidates.Count);
    }

    [Fact]
    public void DifferentSerialIsNotTheSameMonitor()
    {
        var other = Third with { Identity = Third.Identity with { DevicePath = "x", EdidSerialNumber = 99 } };
        Assert.Equal(MatchKind.NotFound, DisplayMatcher.Match(Third.Identity, [other]).Kind);
    }

    [Fact] public void NullIdentityNotFound() => Assert.Equal(MatchKind.NotFound, DisplayMatcher.Match(null, [Aoc]).Kind);
}

public class LauncherPolicyTests
{
    static LauncherDisplaySettings PreferTv() => new() { Preferred = Tv.Identity };

    [Fact]
    public void StartsOnPreferredWhenPresent() =>
        Assert.Same(Tv, LauncherDisplayPolicy.Decide(PreferTv(), Snap(Aoc, Tv), null).Target);

    [Fact]
    public void FallsBackToPrimaryWithoutLosingPreference()
    {
        var s = PreferTv();
        var d = LauncherDisplayPolicy.Decide(s, Snap(Aoc), null);
        Assert.Same(Aoc, d.Target);
        Assert.False(d.PreferredAvailable);
        Assert.Equal(Tv.Identity, s.Preferred);
    }

    [Fact]
    public void InactiveConnectedPreferredIsNotATarget() =>
        Assert.Same(Aoc, LauncherDisplayPolicy.Decide(PreferTv(), Snap(Aoc, Tv with { Status = DisplayStatus.ConnectedInactive }), null).Target);

    [Fact]
    public void MigratesToPreferredWhenItConnects() =>
        Assert.Same(Tv, LauncherDisplayPolicy.Decide(PreferTv(), Snap(Aoc, Tv), host: Aoc).Target);

    [Fact]
    public void AutoSwitchOffStaysOnHost()
    {
        var s = PreferTv(); s.AutoSwitchToPreferred = false;
        Assert.Same(Aoc, LauncherDisplayPolicy.Decide(s, Snap(Aoc, Tv), host: Aoc).Target);
    }

    [Fact]
    public void SpecificFallbackUsedOverPrimary()
    {
        var s = PreferTv(); s.FallbackMode = FallbackMode.SpecificDisplay; s.Fallback = Third.Identity;
        Assert.Same(Third, LauncherDisplayPolicy.Decide(s, Snap(Aoc, Third), null).Target);
    }

    [Fact]
    public void MissingSpecificFallbackUsesPrimary()
    {
        var s = PreferTv(); s.FallbackMode = FallbackMode.SpecificDisplay; s.Fallback = Third.Identity;
        Assert.Same(Aoc, LauncherDisplayPolicy.Decide(s, Snap(Aoc), null).Target);
    }

    [Fact]
    public void ReturnToFallbackOffKeepsCurrentHost()
    {
        var s = PreferTv(); s.ReturnToFallback = false;
        Assert.Same(Third, LauncherDisplayPolicy.Decide(s, Snap(Aoc, Third), host: Third).Target);
    }

    [Fact]
    public void RememberLastDisplayBeatsFallback()
    {
        var s = PreferTv(); s.RememberLastDisplay = true; s.LastDisplay = Third.Identity;
        Assert.Same(Third, LauncherDisplayPolicy.Decide(s, Snap(Aoc, Third), null).Target);
    }

    [Fact]
    public void AmbiguousPreferredRequestsRemap()
    {
        var s = new LauncherDisplaySettings { Preferred = Aoc.Identity with { DevicePath = "gone", ConnectorInstance = 9 } };
        var d = LauncherDisplayPolicy.Decide(s, Snap(Disp("a", "AOC", 0x2590, primary: true, conn: 1), Disp("b", "AOC", 0x2590, conn: 2)), null);
        Assert.True(d.RemapRequired);
        Assert.NotNull(d.Target);
    }

    [Fact]
    public void NegativeCoordinatesAndMixedDpiPreserved()
    {
        var d = LauncherDisplayPolicy.Decide(PreferTv(), Snap(Aoc, Tv, Third), null);
        Assert.Equal(-3840, d.Target!.Bounds.X);
        Assert.Equal(300, d.Target.ScalePercent);
    }
}

public class TvDetectionTests
{
    static DisplayInfo D(string name, string? mfr, string tech, int w) => new()
    {
        Identity = new DisplayIdentity { FriendlyName = name }, Manufacturer = mfr, OutputTechnology = tech, Bounds = new PixelRect(0, 0, w, w * 9 / 16),
    };

    [Fact] public void NameSaysTv() => Assert.True(D("Beyond TV", "TCL", "HDMI", 3840).LooksLikeTv);
    [Fact] public void TvBrand() => Assert.True(D("65C845", "Hisense", "HDMI", 1920).LooksLikeTv);
    [Fact] public void FourKOverHdmi() => Assert.True(D("LG 4K", "LG Electronics", "HDMI", 3840).LooksLikeTv);
    [Fact] public void DisplayPortMonitor() => Assert.False(D("2590G4", "AOC", "DisplayPort", 1920).LooksLikeTv);
    [Fact] public void FourKDisplayPortMonitor() => Assert.False(D("U2723QE", "Dell", "DisplayPort", 3840).LooksLikeTv);
}

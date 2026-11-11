using GamingHub.Core.Displays;
using GamingHub.Core.Games;
using static GamingHub.Tests.TestData;

namespace GamingHub.Tests;

public class PlacementPlannerTests
{
    static readonly PixelRect AocBounds = Aoc.Bounds;                       // 0,0 1920x1080
    static readonly DisplayInfo TvWithWork = Tv with { WorkArea = new PixelRect(-3840, 0, 3840, 2100) };

    [Fact]
    public void WindowAlreadyOnTargetNeedsNothing() =>
        Assert.Equal(PlacementAction.None, PlacementPlanner.Plan(new PixelRect(100, 100, 1280, 720), AocBounds, Aoc, false).Action);

    [Fact]
    public void BorderlessFullscreenCoversTargetMonitorIncludingNegativeCoordinates()
    {
        var p = PlacementPlanner.Plan(AocBounds, AocBounds, TvWithWork, false);
        Assert.Equal((PlacementAction.Move, Tv.Bounds), (p.Action, p.Target));
        Assert.Equal(-3840, p.Target.X);
    }

    [Fact]
    public void WindowedIsCenteredOnTargetWorkAreaKeepingSize()
    {
        var p = PlacementPlanner.Plan(new PixelRect(200, 100, 1280, 720), AocBounds, TvWithWork, false);
        Assert.Equal(new PixelRect(-3840 + (3840 - 1280) / 2, (2100 - 720) / 2, 1280, 720), p.Target);
    }

    [Fact]
    public void OversizedWindowIsClampedToWorkArea()
    {
        var third = Third with { WorkArea = new PixelRect(1920, 0, 2560, 1400) };
        var p = PlacementPlanner.Plan(new PixelRect(0, 0, 3000, 1600), new PixelRect(0, 0, 3840, 2160), third, false);
        Assert.Equal((2560, 1400), (p.Target.Width, p.Target.Height));
    }

    [Fact]
    public void ExclusiveFullscreenIsReportedUnsupported() =>
        Assert.Equal(PlacementAction.Unsupported, PlacementPlanner.Plan(AocBounds, AocBounds, TvWithWork, exclusiveFullscreen: true).Action);

    [Theory]
    [InlineData(0, 0, 1920, 1080, true)]        // fully on
    [InlineData(1000, 0, 1920, 1080, false)]    // less than half on AOC
    [InlineData(-200, 0, 1920, 1080, true)]     // mostly on
    public void VerificationUsesMajorityIntersection(int x, int y, int w, int h, bool expected) =>
        Assert.Equal(expected, PlacementPlanner.IsOn(new PixelRect(x, y, w, h), AocBounds));
}

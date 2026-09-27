using LanConnect.Core.Layout;

namespace LanConnect.Core.Tests;

public class GeometryTests
{
    // The user's main PC: two 1920x1080 monitors side by side.
    private static readonly DesktopLayout TwoMonitors = new([
        RectI.FromSize(0, 0, 1920, 1080),
        RectI.FromSize(1920, 0, 1920, 1080),
    ]);

    [Fact]
    public void BoundaryBetweenMonitorsIsNotAnExit()
    {
        Assert.Null(TwoMonitors.FindExit(new PointI(1919, 500), new PointI(1925, 500)));
    }

    [Fact]
    public void OuterRightEdgeIsAnExit()
    {
        var exit = TwoMonitors.FindExit(new PointI(3835, 540), new PointI(3850, 540));
        Assert.NotNull(exit);
        Assert.Equal(Side.Right, exit!.Value.Side);
        Assert.Equal(11, exit.Value.Overshoot);
        Assert.Equal(0.5, exit.Value.Fraction, 2);
    }

    [Fact]
    public void OuterLeftEdgeIsAnExit()
    {
        var exit = TwoMonitors.FindExit(new PointI(3, 100), new PointI(-4, 100));
        Assert.Equal(Side.Left, exit!.Value.Side);
    }

    [Fact]
    public void UnevenMonitorsUseOutwardFacingEdges()
    {
        // 1440p monitor on the left, 1080p monitor on the right aligned at the top.
        var layout = new DesktopLayout([
            RectI.FromSize(0, 0, 2560, 1440),
            RectI.FromSize(2560, 0, 1920, 1080),
        ]);
        // Moving right from the big monitor below the small one: nothing is there -> outward.
        var exit = layout.FindExit(new PointI(2559, 1300), new PointI(2565, 1300));
        Assert.Equal(Side.Right, exit!.Value.Side);
        // Moving right from the big monitor next to the small one: lands on the small one.
        Assert.Null(layout.FindExit(new PointI(2559, 500), new PointI(2565, 500)));
    }

    [Fact]
    public void EntryPointLandsOnTheCorrectEdgeAndPosition()
    {
        var laptop = new DesktopLayout([RectI.FromSize(0, 0, 1920, 1200)]);
        var entry = laptop.EntryPoint(Side.Left, 0.5);
        Assert.Equal(0, entry.X);
        Assert.InRange(entry.Y, 599, 600);

        var pcEntry = TwoMonitors.EntryPoint(Side.Right, 0.25);
        Assert.Equal(3839, pcEntry.X);
        Assert.Equal(270, pcEntry.Y);
    }

    [Fact]
    public void ClampSnapsToNearestMonitor()
    {
        Assert.Equal(new PointI(3839, 1079), TwoMonitors.Clamp(new PointI(5000, 5000)));
        Assert.Equal(new PointI(10, 10), TwoMonitors.Clamp(new PointI(10, 10)));
    }
}

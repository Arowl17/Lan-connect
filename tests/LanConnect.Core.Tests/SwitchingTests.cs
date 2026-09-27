using LanConnect.Core.Layout;
using LanConnect.Core.Switching;

namespace LanConnect.Core.Tests;

public class SwitchingTests
{
    private static readonly DesktopLayout Pc = new([
        RectI.FromSize(0, 0, 1920, 1080),
        RectI.FromSize(1920, 0, 1920, 1080),
    ]);

    private static readonly Side[] LaptopOnRight = [Side.Right];

    [Fact]
    public void SwitchesAfterPushingPastTheEdge()
    {
        var policy = new EdgeSwitchPolicy { Options = new EdgeOptions { PushDistance = 20 } };
        var from = new PointI(3839, 540);
        Assert.Null(policy.Evaluate(Pc, LaptopOnRight, from, new PointI(3849, 540), 0, blocked: false));
        var decision = policy.Evaluate(Pc, LaptopOnRight, from, new PointI(3849, 540), 10, blocked: false);
        Assert.NotNull(decision);
        Assert.Equal(Side.Right, decision!.Value.Side);
    }

    [Fact]
    public void FastFlickSwitchesImmediately()
    {
        var policy = new EdgeSwitchPolicy();
        Assert.NotNull(policy.Evaluate(Pc, LaptopOnRight, new PointI(3800, 540), new PointI(3900, 540), 0, false));
    }

    [Fact]
    public void SlowPushesTooFarApartDoNotAddUp()
    {
        var policy = new EdgeSwitchPolicy { Options = new EdgeOptions { PushDistance = 20, PushWindowMs = 250 } };
        var from = new PointI(3839, 540);
        Assert.Null(policy.Evaluate(Pc, LaptopOnRight, from, new PointI(3850, 540), 0, false));
        Assert.Null(policy.Evaluate(Pc, LaptopOnRight, from, new PointI(3850, 540), 1000, false));
    }

    [Fact]
    public void CornersNeverSwitch()
    {
        var policy = new EdgeSwitchPolicy();
        // Top-right corner: the close button of a maximized window.
        Assert.Null(policy.Evaluate(Pc, LaptopOnRight, new PointI(3839, 5), new PointI(3990, 5), 0, false));
        // Bottom-right corner: "show desktop".
        Assert.Null(policy.Evaluate(Pc, LaptopOnRight, new PointI(3839, 1075), new PointI(3990, 1075), 0, false));
    }

    [Fact]
    public void SidesWithoutAComputerNeverSwitch()
    {
        var policy = new EdgeSwitchPolicy();
        Assert.Null(policy.Evaluate(Pc, LaptopOnRight, new PointI(0, 540), new PointI(-200, 540), 0, false));
    }

    [Fact]
    public void BlockedNeverSwitchesAndClearsPressure()
    {
        var policy = new EdgeSwitchPolicy { Options = new EdgeOptions { PushDistance = 20 } };
        var from = new PointI(3839, 540);
        Assert.Null(policy.Evaluate(Pc, LaptopOnRight, from, new PointI(3900, 540), 0, blocked: true));
        Assert.Null(policy.Evaluate(Pc, LaptopOnRight, from, new PointI(3850, 540), 5, blocked: false));
    }

    [Theory]
    [InlineData("GeForceNOW.exe", null, true, false, false)]
    [InlineData("geforcenow", null, true, false, false)]
    [InlineData("chrome.exe", "Cyberpunk 2077 - GeForce NOW - Google Chrome", true, false, false)]
    [InlineData("game.exe", "Game", false, false, false)]
    [InlineData("game.exe", "Game", true, true, false)]
    [InlineData("game.exe", "Game", true, false, true)]
    public void GameModeBlocksGames(string process, string? title, bool cursorVisible, bool clipped, bool fullscreen)
    {
        var state = new ForegroundState(process, title, fullscreen, cursorVisible, clipped);
        Assert.NotNull(GameModeRules.BlockReason(state, new GameModeOptions()));
    }

    [Fact]
    public void GameModeAllowsNormalDesktopUse()
    {
        var state = new ForegroundState("explorer.exe", "Documents", false, true, false);
        Assert.Null(GameModeRules.BlockReason(state, new GameModeOptions()));
        Assert.Null(GameModeRules.BlockReason(new ForegroundState("GeForceNOW.exe", null, true, false, true), new GameModeOptions { Enabled = false }));
    }
}

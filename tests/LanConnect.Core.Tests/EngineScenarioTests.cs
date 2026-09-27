using LanConnect.Core.Engine;
using LanConnect.Core.Input;
using LanConnect.Core.Layout;

namespace LanConnect.Core.Tests;

/// <summary>More end-to-end scenarios: two simulated computers over real networking.</summary>
public class EngineScenarioTests
{
    private static Task WaitUntilAsync(Func<bool> condition) => EngineTests.WaitUntilAsync(condition);

    private static async Task<(TestComputer Pc, TestComputer Laptop)> ControllingLaptopAsync()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        pc.Platform.Cursor = new PointI(3800, 540);
        Assert.True(pc.Move(150, 0));
        await WaitUntilAsync(() => laptop.Engine.Status.State == ControlState.ControlledBy);
        await WaitUntilAsync(() => laptop.Platform.LastMoveTarget is { X: 0 });
        return (pc, laptop);
    }

    [Fact]
    public async Task LaggingCursorOnTheLaptopDoesNotLoseMovement()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;
        laptop.Platform.LagMoves = true; // GetCursorPos reports one move behind, like a busy Windows

        pc.Platform.Cursor = new PointI(3800, 540);
        Assert.True(pc.Move(150, 0));
        await WaitUntilAsync(() => laptop.Engine.Status.State == ControlState.ControlledBy);
        await WaitUntilAsync(() => laptop.Platform.LastMoveTarget is { X: 0 });
        var entry = laptop.Platform.LastMoveTarget!.Value;

        int dx = 0, dy = 0;
        for (var i = 0; i < 40; i++)
        {
            pc.Move(7, 3);
            dx += 7;
            dy += 3;
            await Task.Delay(3);
        }
        var expected = new PointI(entry.X + dx, entry.Y + dy);
        await WaitUntilAsync(() => laptop.Platform.LastMoveTarget == expected);
    }

    [Fact]
    public async Task LaptopTouchpadMovementIsFollowed()
    {
        var (pc, laptop) = await ControllingLaptopAsync();
        await using var _ = pc;
        await using var __ = laptop;

        laptop.Platform.Cursor = new PointI(1000, 900); // someone uses the laptop's own touchpad
        pc.Move(5, 5);
        await WaitUntilAsync(() => laptop.Platform.LastMoveTarget == new PointI(1005, 905));
    }

    [Fact]
    public async Task MissedButtonReleaseNeitherBlocksSwitchingNorStealsClicks()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;

        // A click's release was missed (e.g. game mode removed the hook mid-click).
        Assert.False(pc.Engine.HandleMouseEvent(InputEvent.MouseButtonEvent(MouseButton.Left, true)));
        pc.Platform.KeyDown = _ => false; // Windows: no button is actually held

        pc.Platform.Cursor = new PointI(3800, 540);
        Assert.True(pc.Move(150, 0));     // switching isn't blocked by the phantom drag
        Assert.Equal(ControlState.Controlling, pc.Engine.Status.State);

        Assert.True(pc.Engine.HandleMouseEvent(InputEvent.MouseButtonEvent(MouseButton.Left, true)));
        await WaitUntilAsync(() => laptop.Platform.Injected.Any(e => e.Kind == InputEventKind.MouseButton && e.Button == MouseButton.Left && e.Down));
    }

    [Fact]
    public async Task MissedCtrlReleaseDoesNotTurnAltArrowIntoTheHotkey()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;

        pc.Key(VirtualKeys.LControl, true);         // Ctrl pressed, release missed
        pc.Platform.KeyDown = vk => vk == VirtualKeys.LMenu; // really held: only Alt
        pc.Key(VirtualKeys.LMenu, true);
        Assert.False(pc.Key(VirtualKeys.Right, true)); // Alt+Right = browser "forward", not a switch
        Assert.Equal(ControlState.Local, pc.Engine.Status.State);
    }

    [Fact]
    public async Task SwitchToAndReturnHomeFromTheTrayMenu()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;

        Assert.True(pc.Engine.SwitchTo(laptop.Id));
        Assert.Equal(ControlState.Controlling, pc.Engine.Status.State);
        await WaitUntilAsync(() => laptop.Engine.Status.State == ControlState.ControlledBy);

        pc.Engine.ReturnHome();
        Assert.Equal(ControlState.Local, pc.Engine.Status.State);
        await WaitUntilAsync(() => laptop.Engine.Status.State == ControlState.Local);
    }

    [Fact]
    public async Task PausingReturnsControlAndDisablesEdges()
    {
        var (pc, laptop) = await ControllingLaptopAsync();
        await using var _ = pc;
        await using var __ = laptop;

        pc.Engine.Paused = true;
        Assert.Equal(ControlState.Local, pc.Engine.Status.State);
        await WaitUntilAsync(() => laptop.Engine.Status.State == ControlState.Local);

        pc.Platform.Cursor = new PointI(3800, 540);
        Assert.False(pc.Move(300, 0));
        pc.Key(VirtualKeys.LControl, true);
        pc.Key(VirtualKeys.LMenu, true);
        Assert.False(pc.Key(VirtualKeys.Right, true)); // hotkey off too while paused
        pc.Key(VirtualKeys.Right, false);
        pc.Key(VirtualKeys.LMenu, false);
        pc.Key(VirtualKeys.LControl, false);

        pc.Engine.Paused = false;
        await WaitUntilAsync(() => pc.Platform.MouseHookEnabled);
        pc.Platform.Cursor = new PointI(3800, 540);
        Assert.True(pc.Move(300, 0));
    }

    [Fact]
    public async Task PlacementChangesSyncToTheOtherComputer()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;

        pc.Engine.SetPlacement(laptop.Id, Side.Top); // laptop moved above the PC's screens
        await WaitUntilAsync(() => laptop.Settings.Snapshot().Devices.Single().Placement == Side.Bottom);

        // Edge switching follows: the top edge now leads to the laptop, the right edge doesn't.
        await WaitUntilAsync(() => pc.Platform.MouseHookEnabled);
        pc.Platform.Cursor = new PointI(3800, 540);
        Assert.False(pc.Move(300, 0));
        pc.Platform.Cursor = new PointI(960, 3);
        Assert.True(pc.Move(0, -100));
        Assert.Equal(ControlState.Controlling, pc.Engine.Status.State);
    }

    [Fact]
    public async Task ForgettingUnpairsBothComputers()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;

        await pc.Engine.ForgetAsync(laptop.Id);
        Assert.Empty(pc.Settings.Snapshot().Devices);
        await WaitUntilAsync(() => laptop.Settings.Snapshot().Devices.Count == 0);
        await WaitUntilAsync(() => !pc.Engine.IsConnected(laptop.Id) && !laptop.Engine.IsConnected(pc.Id));
    }
}

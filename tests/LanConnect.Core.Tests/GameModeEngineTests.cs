using LanConnect.Core.Engine;
using LanConnect.Core.Layout;
using LanConnect.Core.Switching;

namespace LanConnect.Core.Tests;

/// <summary>
/// Skyrim in borderless fullscreen on the PC's right monitor, the laptop to its right: the game
/// hides the cursor in its menus too and never locks it, and the screen it runs on borders the
/// laptop.
/// </summary>
public class GameModeEngineTests
{
    private const ushort Esc = 0x1B;
    private const nint SkyrimWindow = 0x1234;
    private static readonly RectI RightMonitor = RectI.FromSize(1920, 0, 1920, 1080);

    private static ForegroundState Skyrim() =>
        new("SkyrimSE.exe", "Skyrim Special Edition", true, false, false, SkyrimWindow, RightMonitor);

    /// <summary>Moves the hidden cursor back and forth like looking around does, between engine ticks.</summary>
    private static async Task LookAroundUntilAsync(TestComputer pc, Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var step = 40;
        while (!condition())
        {
            var c = pc.Platform.Cursor;
            if (c.X + step < RightMonitor.Left + 10 || c.X + step > RightMonitor.Right - 10)
            {
                step = -step;
            }
            pc.Platform.Cursor = pc.Platform.KeepInside(new PointI(c.X + step, c.Y));
            await Task.Delay(30, cts.Token);
        }
    }

    private static void Tap(TestComputer pc, ushort vk)
    {
        pc.Key(vk, true);
        pc.Key(vk, false);
    }

    private static async Task<(TestComputer Pc, TestComputer Laptop)> PlayingSkyrimAsync()
    {
        var (pc, laptop) = await EngineTests.SetUpAsync();
        pc.Platform.Foreground = Skyrim();
        pc.Platform.Cursor = new PointI(2500, 500);
        await LookAroundUntilAsync(pc, () => pc.Platform.Confined == RightMonitor);
        await EngineTests.WaitUntilAsync(() => !pc.Platform.MouseHookEnabled);
        Assert.NotNull(pc.Engine.Status.GameModeReason);
        Assert.False(pc.Engine.Status.GameMenuOpen);
        return (pc, laptop);
    }

    [Fact]
    public async Task EscFreesTheMouseAndEscLocksItAgain()
    {
        var (pc, laptop) = await PlayingSkyrimAsync();
        await using var _ = pc;
        await using var __ = laptop;

        // Looking around pins the invisible cursor against the edge that faces the laptop.
        // It stays on the game's screen, and never switches computers.
        pc.Platform.Cursor = pc.Platform.KeepInside(new PointI(5000, 500));
        Assert.Equal(RightMonitor.Right - 1, pc.Platform.Cursor.X);
        Assert.False(pc.Move(150, 0));
        Assert.Equal(ControlState.Local, pc.Engine.Status.State);

        // Esc: the game's menu opens and the mouse is free, starting from the middle of the screen.
        Tap(pc, Esc);
        await EngineTests.WaitUntilAsync(() => pc.Engine.Status.GameMenuOpen && pc.Platform.MouseHookEnabled);
        Assert.Null(pc.Platform.Confined);
        Assert.Null(pc.Engine.Status.GameModeReason);
        Assert.Equal(RightMonitor.Center, pc.Platform.LastPlaced);
        Assert.Equal(RightMonitor.Center, pc.Platform.Cursor);

        // A nudge doesn't switch; moving over to the laptop does.
        Assert.False(pc.Move(30, 0));
        Assert.False(pc.Move(900, 0));
        Assert.True(pc.Move(150, 0));
        Assert.Equal(ControlState.Controlling, pc.Engine.Status.State);

        // Come back from the laptop: the menu is still open, so the mouse stays free.
        await EngineTests.WaitUntilAsync(() => laptop.Engine.Status.State == ControlState.ControlledBy);
        await EngineTests.WaitUntilAsync(() => laptop.Platform.Cursor.X == 0);
        Assert.True(pc.Move(-150, 0)); // the PC's mouse pushes back through the laptop's left edge
        await EngineTests.WaitUntilAsync(() => pc.Engine.Status.State == ControlState.Local);
        await Task.Delay(400); // a few engine ticks
        Assert.True(pc.Engine.Status.GameMenuOpen);
        Assert.Null(pc.Platform.Confined);

        // Esc again: back in the game, the edges are held and the cursor kept on the game's screen.
        Tap(pc, Esc);
        await EngineTests.WaitUntilAsync(() => !pc.Engine.Status.GameMenuOpen && pc.Engine.Status.GameModeReason is not null);
        await EngineTests.WaitUntilAsync(() => !pc.Platform.MouseHookEnabled);
        await LookAroundUntilAsync(pc, () => pc.Platform.Confined == RightMonitor);
    }

    [Fact]
    public async Task LeavingTheGameDoesNotFlingTheCursorOntoTheLaptop()
    {
        var (pc, laptop) = await PlayingSkyrimAsync();
        await using var _ = pc;
        await using var __ = laptop;
        pc.Platform.Cursor = pc.Platform.KeepInside(new PointI(5000, 500)); // pinned at the edge

        // Alt+Tab to the desktop: the cursor is free again, right at the laptop's edge.
        pc.Platform.Foreground = ForegroundState.Desktop;
        await EngineTests.WaitUntilAsync(() => pc.Platform.Confined is null && pc.Platform.MouseHookEnabled);
        Assert.Null(pc.Engine.Status.GameModeReason);

        // Pushing on from there doesn't switch: the cursor first has to come away from the edge.
        Assert.False(pc.Move(150, 0));
        Assert.False(pc.Move(150, 0));
        Assert.Equal(ControlState.Local, pc.Engine.Status.State);
        Assert.False(pc.Move(-300, 0));
        Assert.False(pc.Move(150, 0));
        Assert.True(pc.Move(300, 0));
        Assert.Equal(ControlState.Controlling, pc.Engine.Status.State);
    }

    [Fact]
    public async Task PausingLanConnectLetsGoOfTheGamesCursor()
    {
        var (pc, laptop) = await PlayingSkyrimAsync();
        await using var _ = pc;
        await using var __ = laptop;

        pc.Engine.Paused = true;
        await EngineTests.WaitUntilAsync(() => pc.Platform.Confined is null);
        await Task.Delay(400);
        Assert.Null(pc.Platform.Confined);

        // Escape in the game doesn't change anything while paused.
        Tap(pc, Esc);
        await Task.Delay(300);
        Assert.False(pc.Engine.Status.GameMenuOpen);
    }

    [Fact]
    public async Task TheAppListStillHoldsTheEdgesInAMenu()
    {
        var (pc, laptop) = await PlayingSkyrimAsync();
        await using var _ = pc;
        await using var __ = laptop;
        pc.Settings.Update(s => s.GameMode.BlockedApps.Add("SkyrimSE.exe"));

        Tap(pc, Esc);
        await EngineTests.WaitUntilAsync(() => pc.Engine.Status.GameMenuOpen);
        await Task.Delay(300);
        Assert.NotNull(pc.Engine.Status.GameModeReason);
        Assert.False(pc.Move(2000, 0));
        Assert.Equal(ControlState.Local, pc.Engine.Status.State);
    }
}

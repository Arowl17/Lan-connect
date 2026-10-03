using LanConnect.Core.Input;
using LanConnect.Core.Layout;
using LanConnect.Core.Switching;

namespace LanConnect.Core.Tests;

public class GameSessionTests
{
    private const ushort Esc = 0x1B, W = 0x57, D = 0x44, Q = 0x51;
    private const nint Skyrim = 0x1234, Browser = 0x5678;

    private static readonly RectI Main = RectI.FromSize(0, 0, 1920, 1080);
    private static readonly RectI Second = RectI.FromSize(1920, 0, 1920, 1080);

    /// <summary>Skyrim in borderless fullscreen on the main monitor.</summary>
    private static ForegroundState Game(bool cursorVisible = false, bool clipped = false, bool fullscreen = true) =>
        new("SkyrimSE.exe", "Skyrim Special Edition", fullscreen, cursorVisible, clipped, Skyrim, Main);

    private static InputEvent Key(ushort vk, bool down) => InputEvent.KeyEvent((ushort)(vk + 0x100), vk, false, down);

    /// <summary>Looks around in the game until the session keeps the cursor on its screen.</summary>
    private static GameSession Playing()
    {
        var session = new GameSession();
        Assert.Null(session.Update(Game(), new PointI(500, 500), canConfine: true));
        Assert.Null(session.Update(Game(), new PointI(600, 500), canConfine: true)); // moved once
        Assert.Equal(Main, session.Update(Game(), new PointI(700, 500), canConfine: true)); // twice: looking around
        Assert.True(session.IsGame(Skyrim));
        return session;
    }

    private static bool Press(GameSession session, ushort vk, long nowMs, nint foreground = Skyrim, bool shortcut = false)
    {
        var changed = session.OnKey(Key(vk, true), foreground, nowMs, shortcut);
        changed |= session.OnKey(Key(vk, false), foreground, nowMs + 80, shortcut: false);
        return changed;
    }

    [Fact]
    public void KeepsTheCursorOnTheGamesScreenWhileLookingAround()
    {
        var session = Playing();

        // Stays while the game keeps the cursor hidden, moving or not.
        Assert.Equal(Main, session.Update(Game(), new PointI(1919, 500), canConfine: true));
        Assert.Equal(Main, session.Update(Game(), new PointI(1919, 500), canConfine: true));

        // The game shows its own cursor on its screen (a menu it opened by itself): let go.
        Assert.Null(session.Update(Game(cursorVisible: true), new PointI(900, 500), canConfine: true));
    }

    [Fact]
    public void PullsBackACursorThatGotOntoAnotherScreenWhilePlaying()
    {
        var session = Playing();

        // Windows dropped the lock for a moment and the cursor slipped onto the second monitor,
        // where the windows under it show it.
        Assert.Equal(Main, session.Update(Game(cursorVisible: true), new PointI(2500, 500), canConfine: true));
    }

    [Fact]
    public void NeverHoldsAVideoPlayersIdleCursor()
    {
        var session = new GameSession();
        var video = new ForegroundState("chrome.exe", "YouTube", true, false, false, Browser, Main);
        var shown = video with { CursorVisible = true };

        // Hidden while idle, shown as soon as it moves, including onto the other monitor.
        Assert.Null(session.Update(video, new PointI(900, 500), canConfine: true));
        Assert.Null(session.Update(video, new PointI(900, 500), canConfine: true));
        Assert.Null(session.Update(shown, new PointI(1500, 500), canConfine: true));
        Assert.Null(session.Update(shown, new PointI(2500, 500), canConfine: true));
        Assert.False(session.IsGame(Browser));
    }

    [Fact]
    public void LeavesGamesThatLockTheCursorThemselvesAndWindowedGamesAlone()
    {
        var clipping = new GameSession();
        clipping.Update(Game(clipped: true), new PointI(500, 500), canConfine: true);
        clipping.Update(Game(clipped: true), new PointI(600, 500), canConfine: true);
        Assert.Null(clipping.Update(Game(clipped: true), new PointI(700, 500), canConfine: true));

        var windowed = new GameSession();
        windowed.Update(Game(fullscreen: false), new PointI(500, 500), canConfine: true);
        windowed.Update(Game(fullscreen: false), new PointI(600, 500), canConfine: true);
        Assert.Null(windowed.Update(Game(fullscreen: false), new PointI(700, 500), canConfine: true));
    }

    [Fact]
    public void LetsGoWhileControllingAnotherComputerOrWhenTurnedOff()
    {
        var session = Playing();
        Assert.Null(session.Update(Game(), new PointI(800, 500), canConfine: false));

        var off = Playing();
        off.Options = new GameModeOptions { KeepCursorOnGameScreen = false };
        Assert.Null(off.Update(Game(), new PointI(800, 500), canConfine: true));
    }

    [Fact]
    public void EscOpensAndClosesTheMenu()
    {
        var session = Playing();

        Assert.True(Press(session, Esc, 1000));
        Assert.True(session.IsMenuOpen(Skyrim));
        // The menu starts with the invisible cursor in the middle of the game's screen, away
        // from any edge it may have been pinned against.
        Assert.Equal(Main.Center, session.TakeRecenter());
        Assert.Null(session.TakeRecenter());
        // The mouse is free while the menu is open.
        Assert.Null(session.Update(Game(), new PointI(960, 540), canConfine: true));
        Assert.Null(session.Update(Game(), new PointI(1500, 540), canConfine: true));

        Assert.True(Press(session, Esc, 3000));
        Assert.False(session.IsMenuOpen(Skyrim));
        Assert.Null(session.TakeRecenter());
        // Back in the game: kept on its screen again right away.
        Assert.Equal(Main, session.Update(Game(), new PointI(1500, 540), canConfine: true));
    }

    [Fact]
    public void ClosingTheMenuBringsBackACursorLeftOnAnotherScreen()
    {
        var session = Playing();
        Press(session, Esc, 1000);

        // In the menu, you moved over to the second monitor (the game stays focused).
        Assert.Null(session.Update(Game(cursorVisible: true), new PointI(2500, 500), canConfine: true));

        // Esc: playing again, and the cursor goes back onto the game's screen.
        Press(session, Esc, 3000);
        Assert.Equal(Main, session.Update(Game(cursorVisible: true), new PointI(2500, 500), canConfine: true));
    }

    [Fact]
    public void ClosingTheMenuLeavesAGamesOwnVisibleCursorAlone()
    {
        var session = Playing();
        Press(session, Esc, 1000);
        Press(session, Esc, 3000);

        // A game that shows its cursor on its own screen (say, a "Resume" button it still shows).
        Assert.Null(session.Update(Game(cursorVisible: true), new PointI(900, 500), canConfine: true));
        Assert.Null(session.Update(Game(cursorVisible: true), new PointI(800, 500), canConfine: true));
    }

    [Fact]
    public void EscOnlyCountsInGamesAndNotForWindowsShortcuts()
    {
        var session = Playing();

        Assert.False(Press(session, Esc, 1000, foreground: Browser)); // not a game
        Assert.False(Press(session, Esc, 3000, foreground: 0)); // keys go to another computer
        Assert.False(Press(session, Esc, 5000, shortcut: true)); // Ctrl+Esc opens Start
        Assert.False(session.IsMenuOpen(Skyrim));

        session.Options = new GameModeOptions { FreeMouseOnEscape = false };
        Assert.False(Press(session, Esc, 7000));
        Assert.False(session.IsMenuOpen(Skyrim));
    }

    [Fact]
    public void HoldingEscDoesNotFlipTheMenuBackAndForth()
    {
        var session = Playing();

        Assert.True(session.OnKey(Key(Esc, true), Skyrim, 1000, shortcut: false));
        for (var t = 1500L; t < 3000; t += 33)
        {
            Assert.False(session.OnKey(Key(Esc, true), Skyrim, t, shortcut: false)); // auto-repeat
        }
        session.OnKey(Key(Esc, false), Skyrim, 3000, shortcut: false);
        Assert.True(session.IsMenuOpen(Skyrim));
    }

    [Fact]
    public void AMissedEscReleaseDoesNotSwallowTheNextPress()
    {
        var session = Playing();

        Assert.True(session.OnKey(Key(Esc, true), Skyrim, 1000, shortcut: false)); // release never seen
        Assert.True(session.OnKey(Key(Esc, true), Skyrim, 4000, shortcut: false));
        Assert.False(session.IsMenuOpen(Skyrim));
    }

    [Fact]
    public void WalkingMeansPlayingAgain()
    {
        var session = Playing();
        Press(session, Esc, 1000);
        Assert.True(session.IsMenuOpen(Skyrim));

        // Tapping keys (menu navigation, typing) keeps the menu open.
        Assert.False(Press(session, W, 2000));
        Assert.False(Press(session, D, 2500));
        Assert.True(session.IsMenuOpen(Skyrim));

        // Holding W: Windows repeats the key, and after a moment it counts as walking.
        Assert.False(session.OnKey(Key(W, true), Skyrim, 3000, shortcut: false));
        Assert.True(session.OnKey(Key(W, true), Skyrim, 3500, shortcut: false));
        Assert.False(session.IsMenuOpen(Skyrim));
        session.OnKey(Key(W, false), Skyrim, 3600, shortcut: false);
    }

    [Fact]
    public void ALongPressCountsOnReleaseToo()
    {
        var session = Playing();
        Press(session, Esc, 1000);

        Assert.False(session.OnKey(Key(D, true), Skyrim, 2000, shortcut: false));
        Assert.True(session.OnKey(Key(D, false), Skyrim, 2600, shortcut: false));
        Assert.False(session.IsMenuOpen(Skyrim));

        // Other keys never do.
        Press(session, Esc, 4000);
        Assert.False(session.OnKey(Key(Q, true), Skyrim, 5000, shortcut: false));
        Assert.False(session.OnKey(Key(Q, false), Skyrim, 6000, shortcut: false));
        Assert.True(session.IsMenuOpen(Skyrim));
    }

    [Fact]
    public void TheMenuStaysOpenWhileYouVisitOtherWindows()
    {
        var session = Playing();
        Press(session, Esc, 1000);

        // Alt+Tab to a browser on the other monitor and back.
        var browser = new ForegroundState("chrome.exe", "Docs", false, true, false, Browser, Second);
        Assert.Null(session.Update(browser, new PointI(2500, 500), canConfine: true));
        Assert.Null(session.Update(browser, new PointI(2600, 500), canConfine: true));
        Assert.True(session.IsMenuOpen(Skyrim));
        Assert.Null(session.Update(Game(), new PointI(900, 500), canConfine: true));
        Assert.Null(session.Update(Game(), new PointI(800, 500), canConfine: true));
        Assert.Null(session.Update(Game(), new PointI(700, 500), canConfine: true));
    }

    [Fact]
    public void BlockReasonIgnoresTheGamesLooksWhileItsMenuIsOpen()
    {
        var options = new GameModeOptions();
        Assert.NotNull(GameModeRules.BlockReason(Game(), options));
        Assert.Null(GameModeRules.BlockReason(Game(), options, menuOpen: true));

        // The app list always counts.
        var gfn = new ForegroundState("GeForceNOW.exe", "GeForce NOW", true, false, false, Browser, Main);
        Assert.NotNull(GameModeRules.BlockReason(gfn, options, menuOpen: true));
    }

    [Fact]
    public void NearOutwardEdgeOnlyCountsActiveOutwardEdges()
    {
        var layout = new DesktopLayout([Main, Second]);
        Side[] right = [Side.Right];
        Assert.True(layout.IsNearOutwardEdge(new PointI(3800, 500), right, 100));
        Assert.False(layout.IsNearOutwardEdge(new PointI(3700, 500), right, 100));
        Assert.False(layout.IsNearOutwardEdge(new PointI(1900, 500), right, 100)); // the second monitor is beyond
        Assert.False(layout.IsNearOutwardEdge(new PointI(10, 500), right, 100)); // left edge isn't active
    }
}

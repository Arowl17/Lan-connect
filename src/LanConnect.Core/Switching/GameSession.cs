using LanConnect.Core.Input;
using LanConnect.Core.Layout;

namespace LanConnect.Core.Switching;

/// <summary>
/// Tells playing a game apart from sitting in its menu, and keeps a game's cursor on its screen
/// while you play.
///
/// Games such as Skyrim hide the cursor in their menus just like while you look around, so the
/// cursor can't tell the two apart. Esc can: it opens the menu and closes it again. A window
/// counts as a game once the cursor moved while hidden on its screen; in a game, Esc toggles
/// "menu open", which frees the mouse. Walking (holding W, A, S or D) means you're playing again, which
/// puts things right if a menu was closed some other way.
///
/// The same games hide the cursor without locking it, so while you play the invisible cursor
/// wanders onto other screens and hovers, scrolls or clicks there. While you play, the cursor is
/// kept on the game's screen instead.
///
/// Not thread-safe: the engine calls it under its lock.
/// </summary>
public sealed class GameSession
{
    /// <summary>Holding a movement key at least this long means you're back in the game.</summary>
    public const long WalkResumeMs = 400;

    /// <summary>
    /// A key-down this soon after the previous one is auto-repeat (Windows repeats a held key
    /// after at most 1 s). Later ones are a new press even if the release was never seen.
    /// </summary>
    private const long RepeatGapMs = 1500;

    private const ushort Escape = 0x1B;
    private const int MaxGames = 16;

    private readonly HashSet<nint> _games = new();
    private readonly HashSet<nint> _menuOpen = new();
    private bool _escapeHeld;
    private long _escapeLastDown;
    private ushort _walkKey;
    private long _walkSince;
    private long _walkLastDown;
    private nint _lastWindow;
    private PointI _lastCursor;
    private int _lookStreak;
    private nint _confinedWindow;
    private RectI _confinedScreen;
    private PointI? _recenter;

    public GameModeOptions Options { get; set; } = new();

    /// <summary>The keys <see cref="OnKey"/> needs to see.</summary>
    public static bool Watches(ushort virtualKey) => virtualKey == Escape || IsMovementKey(virtualKey);

    private static bool IsMovementKey(ushort virtualKey) => virtualKey is 0x57 or 0x41 or 0x53 or 0x44; // W A S D

    public bool IsGame(nint window) => window != 0 && _games.Contains(window);

    public bool IsMenuOpen(nint window) => window != 0 && _menuOpen.Contains(window);

    /// <summary>
    /// Takes a fresh look at the foreground (a few times a second). Returns the screen to keep
    /// the cursor on right now, or null to leave it free.
    /// </summary>
    /// <param name="canConfine">The mouse is working on this computer (not paused, not controlling another one).</param>
    public RectI? Update(in ForegroundState state, PointI cursor, bool canConfine)
    {
        var window = state.Window;
        var onScreen = !state.Screen.IsEmpty && state.Screen.Contains(cursor);
        var hidden = !state.CursorVisible;
        var moved = window != 0 && window == _lastWindow && cursor != _lastCursor;
        _lastWindow = window;
        _lastCursor = cursor;

        // Looking around: the cursor keeps moving while hidden on the window's screen. Video
        // players and "hide while typing" hide an idle cursor, but show it as soon as it moves.
        _lookStreak = hidden && onScreen && moved ? Math.Min(_lookStreak + 1, 2) : 0;
        if (_lookStreak > 0 && Options.Enabled)
        {
            Track(window);
        }

        // Start once the game has been seen looking around twice in a row. Then keep going while
        // the cursor is hidden, or has got off the game's screen anyway (it shows over the other
        // screens' windows). A visible cursor on the game's screen is the game's own menu cursor:
        // let go.
        var keep = canConfine && Options.Enabled && Options.KeepCursorOnGameScreen
            && IsGame(window) && !IsMenuOpen(window)
            && state.IsFullscreen && !state.CursorClipped && !state.Screen.IsEmpty
            && (_confinedWindow == window ? hidden || !onScreen : _lookStreak >= 2);
        if (keep)
        {
            _confinedWindow = window;
            _confinedScreen = state.Screen;
            return state.Screen;
        }
        _confinedWindow = 0;
        return null;
    }

    /// <summary>
    /// Watches Esc and the movement keys (see <see cref="Watches"/>). Returns true when a game's
    /// menu opened or closed.
    /// </summary>
    /// <param name="foreground">The focused window, or 0 while keys don't reach this computer's apps.</param>
    /// <param name="shortcut">Ctrl, Alt or Windows is held: Ctrl+Esc, Alt+Esc and the like belong to Windows.</param>
    public bool OnKey(in InputEvent e, nint foreground, long nowMs, bool shortcut)
    {
        if (e.Kind != InputEventKind.Key)
        {
            return false;
        }

        if (e.VirtualKey == Escape)
        {
            if (!e.Down)
            {
                _escapeHeld = false;
                return false;
            }
            var repeat = _escapeHeld && nowMs - _escapeLastDown < RepeatGapMs;
            _escapeHeld = true;
            _escapeLastDown = nowMs;
            if (repeat || shortcut || !Options.Enabled || !Options.FreeMouseOnEscape || !IsGame(foreground))
            {
                return false;
            }
            if (CloseMenu(foreground))
            {
                return true;
            }
            _menuOpen.Add(foreground);
            if (_confinedWindow == foreground)
            {
                // The invisible cursor could be anywhere on the game's screen, often pinned
                // against an edge from looking around. Start the menu from the middle, so
                // nudging the mouse never slips onto another screen or computer by surprise.
                _recenter = _confinedScreen.Center;
                _confinedWindow = 0;
            }
            return true;
        }

        if (!IsMovementKey(e.VirtualKey))
        {
            return false;
        }
        if (!e.Down)
        {
            if (e.VirtualKey != _walkKey)
            {
                return false;
            }
            _walkKey = 0;
            return nowMs - _walkSince >= WalkResumeMs && CloseMenu(foreground);
        }
        if (e.VirtualKey != _walkKey || nowMs - _walkLastDown >= RepeatGapMs)
        {
            _walkKey = e.VirtualKey;
            _walkSince = nowMs;
        }
        _walkLastDown = nowMs;
        return nowMs - _walkSince >= WalkResumeMs && CloseMenu(foreground);
    }

    /// <summary>Back to playing: hold the cursor right away, wherever the menu left it.</summary>
    private bool CloseMenu(nint window)
    {
        if (!_menuOpen.Remove(window))
        {
            return false;
        }
        _confinedWindow = window;
        return true;
    }

    /// <summary>Where to put the cursor because a game's menu just opened (once), or null.</summary>
    public PointI? TakeRecenter()
    {
        var spot = _recenter;
        _recenter = null;
        return spot;
    }

    private void Track(nint window)
    {
        if (_games.Add(window) && _games.Count > MaxGames)
        {
            // Window handles of closed games pile up; start over (the current one stays).
            _games.Clear();
            _menuOpen.RemoveWhere(w => w != window);
            _games.Add(window);
        }
    }
}

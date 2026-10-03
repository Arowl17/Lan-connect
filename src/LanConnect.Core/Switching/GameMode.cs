using LanConnect.Core.Layout;

namespace LanConnect.Core.Switching;

/// <summary>What the foreground app and cursor look like at the moment of an edge crossing.</summary>
/// <param name="CursorClipped">Locked inside a rectangle by an app (not by Lan Connect itself).</param>
/// <param name="Window">Identifies the foreground window (0 = none or the desktop).</param>
/// <param name="Screen">The monitor the foreground window is on (empty when unknown).</param>
public readonly record struct ForegroundState(
    string? ProcessName,
    string? WindowTitle,
    bool IsFullscreen,
    bool CursorVisible,
    bool CursorClipped,
    nint Window = 0,
    RectI Screen = default)
{
    public static ForegroundState Desktop => new(null, null, false, true, false);
}

public sealed class GameModeOptions
{
    /// <summary>Master switch. When off, edges always switch (like Lan Mouse).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Games hide the cursor while you aim / look around.</summary>
    public bool BlockWhenCursorHidden { get; set; } = true;

    /// <summary>Games often lock the cursor inside their window.</summary>
    public bool BlockWhenCursorLocked { get; set; } = true;

    /// <summary>A focused app covering a whole monitor (borderless or exclusive fullscreen).</summary>
    public bool BlockWhenFullscreen { get; set; } = true;

    /// <summary>
    /// Some games (Skyrim, for example) hide the cursor but leave it free to wander onto other
    /// screens while you look around. Keep it on the game's screen while you play.
    /// </summary>
    public bool KeepCursorOnGameScreen { get; set; } = true;

    /// <summary>
    /// Esc in a game opens its menu: the mouse is free (edges work) until Esc closes it again.
    /// </summary>
    public bool FreeMouseOnEscape { get; set; } = true;

    /// <summary>Executable names (e.g. "GeForceNOW.exe") where edge switching is always off.</summary>
    public List<string> BlockedApps { get; set; } = DefaultBlockedApps();

    /// <summary>Window-title fragments where edge switching is always off (e.g. GeForce NOW in a browser).</summary>
    public List<string> BlockedTitles { get; set; } = ["GeForce NOW"];

    public static List<string> DefaultBlockedApps() => ["GeForceNOW.exe"];
}

public static class GameModeRules
{
    /// <summary>Returns why edge switching is blocked right now, or null when it is allowed.</summary>
    /// <param name="menuOpen">
    /// The player opened the game's menu with Esc: only the app list still counts, since the
    /// game keeps looking like a game (hidden cursor, fullscreen) while it is paused.
    /// </param>
    public static string? BlockReason(in ForegroundState state, GameModeOptions options, bool menuOpen = false)
    {
        if (!options.Enabled)
        {
            return null;
        }

        if (state.ProcessName is { Length: > 0 } process)
        {
            foreach (var app in options.BlockedApps)
            {
                if (SameExecutable(process, app))
                {
                    return $"{app} is in the game list";
                }
            }
        }

        if (state.WindowTitle is { Length: > 0 } title)
        {
            foreach (var fragment in options.BlockedTitles)
            {
                if (fragment.Length > 0 && title.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return $"\"{fragment}\" window is focused";
                }
            }
        }

        if (menuOpen)
        {
            return null;
        }

        if (options.BlockWhenCursorHidden && !state.CursorVisible)
        {
            return "cursor is hidden (in-game)";
        }

        if (options.BlockWhenCursorLocked && state.CursorClipped)
        {
            return "cursor is locked to a window";
        }

        if (options.BlockWhenFullscreen && state.IsFullscreen)
        {
            return "a fullscreen app is focused";
        }

        return null;
    }

    private static bool SameExecutable(string process, string configured)
    {
        static string Normalize(string name)
        {
            name = Path.GetFileName(name.Trim());
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }
        return string.Equals(Normalize(process), Normalize(configured), StringComparison.OrdinalIgnoreCase);
    }
}

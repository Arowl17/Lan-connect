using LanConnect.Core.Layout;

namespace LanConnect.Core.Switching;

public sealed class EdgeOptions
{
    /// <summary>How far (px) you must push past the edge before switching. 0 = instant.</summary>
    public int PushDistance { get; set; } = 20;

    /// <summary>Pushes further apart than this (ms) don't add up.</summary>
    public int PushWindowMs { get; set; } = 250;

    /// <summary>Size (px) of the "dead" zone at each end of an edge, so corners never switch.</summary>
    public int DeadCornerSize { get; set; } = 40;
}

public readonly record struct SwitchDecision(Side Side, double Fraction, PointI EdgePoint);

/// <summary>
/// Decides when pushing against a screen edge should move control to another computer.
/// Unlike Lan Mouse's "crossed the edge = switch" rule, it ignores corners, needs a small push,
/// never switches while a mouse button is held (dragging) and respects game mode.
/// </summary>
public sealed class EdgeSwitchPolicy
{
    private int _pressure;
    private long _lastPushMs;
    private Side? _pressureSide;

    public EdgeOptions Options { get; set; } = new();

    /// <param name="layout">This computer's monitors.</param>
    /// <param name="activeSides">Sides that have a connected computer.</param>
    /// <param name="from">Current cursor position.</param>
    /// <param name="to">Where the mouse is trying to move (may be off screen).</param>
    /// <param name="nowMs">Monotonic time in milliseconds.</param>
    /// <param name="blocked">Game mode or a held button: never switch, and drop any built-up push.</param>
    public SwitchDecision? Evaluate(DesktopLayout layout, IReadOnlyCollection<Side> activeSides, PointI from, PointI to, long nowMs, bool blocked)
    {
        var exit = layout.FindExit(from, to);
        if (exit is not { } e || blocked || !activeSides.Contains(e.Side) || e.DistanceToCorner < Options.DeadCornerSize)
        {
            if (exit is null || blocked)
            {
                Reset();
            }
            return null;
        }

        if (_pressureSide != e.Side || nowMs - _lastPushMs > Options.PushWindowMs)
        {
            _pressure = 0;
        }
        _pressureSide = e.Side;
        _lastPushMs = nowMs;
        _pressure += Math.Max(1, e.Overshoot);

        if (_pressure < Math.Max(1, Options.PushDistance))
        {
            return null;
        }

        Reset();
        return new SwitchDecision(e.Side, e.Fraction, e.From);
    }

    /// <summary>Only checks geometry: would this movement touch an active outward edge at all?</summary>
    public static bool TouchesActiveEdge(DesktopLayout layout, IReadOnlyCollection<Side> activeSides, PointI from, PointI to) =>
        layout.FindExit(from, to) is { } e && activeSides.Contains(e.Side);

    public void Reset()
    {
        _pressure = 0;
        _pressureSide = null;
        _lastPushMs = 0;
    }
}

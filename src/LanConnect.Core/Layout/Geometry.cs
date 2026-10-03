namespace LanConnect.Core.Layout;

public enum Side : byte
{
    Left = 0,
    Right = 1,
    Top = 2,
    Bottom = 3,
}

public static class SideExtensions
{
    public static Side Opposite(this Side side) => side switch
    {
        Side.Left => Side.Right,
        Side.Right => Side.Left,
        Side.Top => Side.Bottom,
        _ => Side.Top,
    };

    public static bool IsHorizontal(this Side side) => side is Side.Left or Side.Right;
}

public readonly record struct PointI(int X, int Y);

/// <summary>Screen rectangle in physical pixels. Right and Bottom are exclusive.</summary>
public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsEmpty => Right <= Left || Bottom <= Top;

    public PointI Center => new(Left + Width / 2, Top + Height / 2);

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public bool Contains(PointI p) => Contains(p.X, p.Y);

    public static RectI FromSize(int left, int top, int width, int height) => new(left, top, left + width, top + height);

    public override string ToString() => $"[{Left},{Top} {Width}x{Height}]";
}

/// <summary>Where the cursor tried to leave the desktop.</summary>
public readonly record struct EdgeExit(Side Side, RectI Monitor, PointI From, int Overshoot, double Fraction)
{
    /// <summary>Distance (px) from the exit point to the nearest end of the monitor's edge.</summary>
    public int DistanceToCorner
    {
        get
        {
            if (Side.IsHorizontal())
            {
                return Math.Min(From.Y - Monitor.Top, Monitor.Bottom - 1 - From.Y);
            }
            return Math.Min(From.X - Monitor.Left, Monitor.Right - 1 - From.X);
        }
    }
}

/// <summary>
/// The arrangement of all monitors of one computer (virtual desktop, physical pixels).
/// An edge "faces outward" on a side when nothing lies beyond it in that direction, which
/// handles side-by-side, stacked and uneven multi-monitor setups.
/// </summary>
public sealed class DesktopLayout
{
    public DesktopLayout(IReadOnlyList<RectI> monitors)
    {
        if (monitors.Count == 0)
        {
            throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        }
        Monitors = monitors;
        Bounds = new RectI(
            monitors.Min(m => m.Left),
            monitors.Min(m => m.Top),
            monitors.Max(m => m.Right),
            monitors.Max(m => m.Bottom));
    }

    public IReadOnlyList<RectI> Monitors { get; }

    public RectI Bounds { get; }

    public bool Contains(PointI p)
    {
        foreach (var m in Monitors)
        {
            if (m.Contains(p))
            {
                return true;
            }
        }
        return false;
    }

    public RectI? MonitorAt(PointI p)
    {
        foreach (var m in Monitors)
        {
            if (m.Contains(p))
            {
                return m;
            }
        }
        return null;
    }

    /// <summary>
    /// Checks whether moving from <paramref name="from"/> (on screen) towards <paramref name="to"/>
    /// (possibly off screen) pushes through an outward-facing edge.
    /// </summary>
    public EdgeExit? FindExit(PointI from, PointI to)
    {
        var monitor = MonitorAt(from);
        if (monitor is not { } m || Contains(to))
        {
            return null;
        }

        // Overshoot beyond the monitor's edges on each axis; pick the dominant one.
        var overLeft = m.Left - to.X;
        var overRight = to.X - (m.Right - 1);
        var overTop = m.Top - to.Y;
        var overBottom = to.Y - (m.Bottom - 1);

        Side side;
        int overshoot;
        var horizontal = Math.Max(overLeft, overRight);
        var vertical = Math.Max(overTop, overBottom);
        if (horizontal <= 0 && vertical <= 0)
        {
            return null;
        }
        if (horizontal >= vertical)
        {
            side = overLeft > 0 ? Side.Left : Side.Right;
            overshoot = horizontal;
        }
        else
        {
            side = overTop > 0 ? Side.Top : Side.Bottom;
            overshoot = vertical;
        }

        // The point where the cursor meets the edge (clamped onto the monitor).
        var edgePoint = new PointI(Math.Clamp(to.X, m.Left, m.Right - 1), Math.Clamp(to.Y, m.Top, m.Bottom - 1));
        if (!FacesOutward(m, side, side.IsHorizontal() ? edgePoint.Y : edgePoint.X))
        {
            // Another monitor lies beyond this edge (but the target point fell in a gap).
            return null;
        }

        return new EdgeExit(side, m, edgePoint, overshoot, FractionAlong(side, edgePoint));
    }

    /// <summary>
    /// Whether <paramref name="p"/> is within <paramref name="distance"/> pixels of an
    /// outward-facing edge on one of the given sides.
    /// </summary>
    public bool IsNearOutwardEdge(PointI p, IReadOnlyCollection<Side> sides, int distance)
    {
        foreach (var side in sides)
        {
            var probe = side switch
            {
                Side.Left => new PointI(p.X - distance, p.Y),
                Side.Right => new PointI(p.X + distance, p.Y),
                Side.Top => new PointI(p.X, p.Y - distance),
                _ => new PointI(p.X, p.Y + distance),
            };
            if (FindExit(p, probe) is { } exit && exit.Side == side)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True when no other monitor lies beyond <paramref name="m"/>'s edge on that side at that coordinate.</summary>
    public bool FacesOutward(RectI m, Side side, int along)
    {
        foreach (var other in Monitors)
        {
            if (other == m)
            {
                continue;
            }
            var overlaps = side.IsHorizontal()
                ? along >= other.Top && along < other.Bottom
                : along >= other.Left && along < other.Right;
            if (!overlaps)
            {
                continue;
            }
            var beyond = side switch
            {
                Side.Left => other.Right <= m.Left,
                Side.Right => other.Left >= m.Right,
                Side.Top => other.Bottom <= m.Top,
                _ => other.Top >= m.Bottom,
            };
            if (beyond)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Position along the desktop's extent on the edge axis, 0..1.</summary>
    public double FractionAlong(Side side, PointI p)
    {
        if (side.IsHorizontal())
        {
            return Bounds.Height <= 1 ? 0.5 : Math.Clamp((p.Y - Bounds.Top) / (double)(Bounds.Height - 1), 0, 1);
        }
        return Bounds.Width <= 1 ? 0.5 : Math.Clamp((p.X - Bounds.Left) / (double)(Bounds.Width - 1), 0, 1);
    }

    /// <summary>
    /// The point just inside the outward edge on <paramref name="side"/> at the given fraction:
    /// where the cursor appears when it arrives from a computer on that side.
    /// </summary>
    public PointI EntryPoint(Side side, double fraction)
    {
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0.5;
        var horizontal = side.IsHorizontal();
        var along = horizontal
            ? Bounds.Top + (int)Math.Round(fraction * (Bounds.Height - 1))
            : Bounds.Left + (int)Math.Round(fraction * (Bounds.Width - 1));

        // Find the outward edge that covers this coordinate; otherwise the closest one.
        RectI? best = null;
        var bestDistance = int.MaxValue;
        var bestAlong = along;
        foreach (var m in Monitors)
        {
            var lo = horizontal ? m.Top : m.Left;
            var hi = (horizontal ? m.Bottom : m.Right) - 1;
            var clamped = Math.Clamp(along, lo, hi);
            if (!FacesOutward(m, side, clamped))
            {
                continue;
            }
            var distance = Math.Abs(clamped - along);
            // Prefer the monitor furthest out on that side when several cover the coordinate.
            var better = distance < bestDistance
                || (distance == bestDistance && best is { } b && side switch
                {
                    Side.Left => m.Left < b.Left,
                    Side.Right => m.Right > b.Right,
                    Side.Top => m.Top < b.Top,
                    _ => m.Bottom > b.Bottom,
                });
            if (better)
            {
                best = m;
                bestDistance = distance;
                bestAlong = clamped;
            }
        }

        var target = best ?? Monitors[0];
        return Clamp(side switch
        {
            Side.Left => new PointI(target.Left, bestAlong),
            Side.Right => new PointI(target.Right - 1, bestAlong),
            Side.Top => new PointI(bestAlong, target.Top),
            _ => new PointI(bestAlong, target.Bottom - 1),
        });
    }

    /// <summary>Clamps a point onto the nearest monitor.</summary>
    public PointI Clamp(PointI p)
    {
        if (Contains(p))
        {
            return p;
        }
        PointI best = p;
        var bestDistance = long.MaxValue;
        foreach (var m in Monitors)
        {
            var c = new PointI(Math.Clamp(p.X, m.Left, m.Right - 1), Math.Clamp(p.Y, m.Top, m.Bottom - 1));
            long dx = c.X - p.X, dy = c.Y - p.Y;
            var d = dx * dx + dy * dy;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
            }
        }
        return best;
    }
}

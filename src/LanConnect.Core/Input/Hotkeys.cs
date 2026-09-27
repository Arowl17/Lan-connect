using LanConnect.Core.Layout;

namespace LanConnect.Core.Input;

[Flags]
public enum HotkeyModifiers : byte
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>Windows virtual-key codes the core logic needs to know about.</summary>
public static class VirtualKeys
{
    public const ushort Shift = 0x10, Control = 0x11, Menu = 0x12;
    public const ushort Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28;
    public const ushort LWin = 0x5B, RWin = 0x5C;
    public const ushort LShift = 0xA0, RShift = 0xA1, LControl = 0xA2, RControl = 0xA3, LMenu = 0xA4, RMenu = 0xA5;

    public static HotkeyModifiers ModifierOf(ushort vk) => vk switch
    {
        Control or LControl or RControl => HotkeyModifiers.Ctrl,
        Menu or LMenu or RMenu => HotkeyModifiers.Alt,
        Shift or LShift or RShift => HotkeyModifiers.Shift,
        LWin or RWin => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    public static Side? ArrowSide(ushort vk) => vk switch
    {
        Left => Side.Left,
        Right => Side.Right,
        Up => Side.Top,
        Down => Side.Bottom,
        _ => null,
    };
}

/// <summary>
/// Detects the "switch computer" chord: the configured modifiers plus an arrow key pointing in
/// the direction of the computer to switch to (or back home). Tracks modifier state itself from
/// the raw key stream, because keys swallowed by the hook never update Windows' own key state.
/// </summary>
public sealed class SwitchHotkeyDetector
{
    private readonly HashSet<ushort> _heldModifierKeys = new();
    private readonly HashSet<ushort> _heldArrows = new();

    public HotkeyModifiers Modifiers { get; set; } = HotkeyModifiers.Ctrl | HotkeyModifiers.Alt;

    public HotkeyModifiers CurrentModifiers
    {
        get
        {
            var mods = HotkeyModifiers.None;
            foreach (var vk in _heldModifierKeys)
            {
                mods |= VirtualKeys.ModifierOf(vk);
            }
            return mods;
        }
    }

    /// <summary>
    /// Feed every key event. Returns the arrow direction when this event completes the chord
    /// (first press only, never on auto-repeat).
    /// </summary>
    public Side? Feed(in InputEvent e)
    {
        if (e.Kind != InputEventKind.Key)
        {
            return null;
        }

        if (VirtualKeys.ModifierOf(e.VirtualKey) != HotkeyModifiers.None)
        {
            if (e.Down)
            {
                _heldModifierKeys.Add(e.VirtualKey);
            }
            else
            {
                _heldModifierKeys.Remove(e.VirtualKey);
            }
            return null;
        }

        var side = VirtualKeys.ArrowSide(e.VirtualKey);
        if (side is null)
        {
            return null;
        }

        if (!e.Down)
        {
            _heldArrows.Remove(e.VirtualKey);
            return null;
        }

        var isRepeat = !_heldArrows.Add(e.VirtualKey);
        if (isRepeat || Modifiers == HotkeyModifiers.None || CurrentModifiers != Modifiers)
        {
            return null;
        }
        return side;
    }

    public void Reset()
    {
        _heldModifierKeys.Clear();
        _heldArrows.Clear();
    }
}

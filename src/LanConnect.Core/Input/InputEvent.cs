namespace LanConnect.Core.Input;

public enum InputEventKind : byte
{
    MouseMove = 1,
    MouseButton = 2,
    MouseWheel = 3,
    Key = 4,
}

public enum MouseButton : byte
{
    Left = 0,
    Right = 1,
    Middle = 2,
    X1 = 3,
    X2 = 4,
}

/// <summary>
/// A single mouse or keyboard event. Keys carry both the hardware scan code (preferred for
/// injection, layout independent) and the virtual key code (fallback when the scan code is 0,
/// e.g. some media keys).
/// </summary>
public readonly record struct InputEvent
{
    public InputEventKind Kind { get; private init; }

    /// <summary>MouseMove: horizontal delta. MouseWheel: wheel delta (120 = one notch).</summary>
    public int Dx { get; private init; }

    /// <summary>MouseMove: vertical delta.</summary>
    public int Dy { get; private init; }

    public MouseButton Button { get; private init; }

    /// <summary>MouseWheel: true for horizontal scrolling.</summary>
    public bool Horizontal { get; private init; }

    /// <summary>MouseButton / Key: true when pressed, false when released.</summary>
    public bool Down { get; private init; }

    public ushort ScanCode { get; private init; }

    public ushort VirtualKey { get; private init; }

    /// <summary>Key: extended-key flag (right Ctrl/Alt, arrows, Insert/Delete, ...).</summary>
    public bool Extended { get; private init; }

    public int WheelDelta => Dx;

    public static InputEvent Move(int dx, int dy) =>
        new() { Kind = InputEventKind.MouseMove, Dx = dx, Dy = dy };

    public static InputEvent MouseButtonEvent(MouseButton button, bool down) =>
        new() { Kind = InputEventKind.MouseButton, Button = button, Down = down };

    public static InputEvent Wheel(int delta, bool horizontal) =>
        new() { Kind = InputEventKind.MouseWheel, Dx = delta, Horizontal = horizontal };

    public static InputEvent KeyEvent(ushort scanCode, ushort virtualKey, bool extended, bool down) =>
        new()
        {
            Kind = InputEventKind.Key,
            ScanCode = scanCode,
            VirtualKey = virtualKey,
            Extended = extended,
            Down = down,
        };

    /// <summary>Identity of the physical key, used to pair presses with releases.</summary>
    public KeyId KeyId => KeyId.From(ScanCode, VirtualKey, Extended);

    public override string ToString() => Kind switch
    {
        InputEventKind.MouseMove => $"move({Dx},{Dy})",
        InputEventKind.MouseButton => $"button({Button},{(Down ? "down" : "up")})",
        InputEventKind.MouseWheel => $"wheel({Dx}{(Horizontal ? ",h" : "")})",
        InputEventKind.Key => $"key(sc=0x{ScanCode:x2}{(Extended ? "e" : "")},vk=0x{VirtualKey:x2},{(Down ? "down" : "up")})",
        _ => "unknown",
    };
}

/// <summary>Identifies a physical key independent of press state.</summary>
public readonly record struct KeyId(uint Value)
{
    public static KeyId From(ushort scanCode, ushort virtualKey, bool extended) =>
        scanCode != 0
            ? new KeyId(scanCode | (extended ? 0x1_0000u : 0u))
            : new KeyId(0x100_0000u | virtualKey);
}

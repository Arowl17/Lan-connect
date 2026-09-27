namespace LanConnect.Core.Input;

public enum InputRoute
{
    /// <summary>Let the event reach this computer normally.</summary>
    Local,

    /// <summary>Swallow locally and forward to the computer being controlled.</summary>
    Remote,

    /// <summary>Swallow and drop (hotkey keys, releases whose press went elsewhere).</summary>
    Swallow,
}

/// <summary>
/// Decides, for every key and mouse-button event on the controlling computer, whether it goes to
/// this computer or to the remote one - while guaranteeing that every press and its release end
/// up on the same computer. That rule is what prevents stuck keys:
/// <list type="bullet">
/// <item>Keys already held when control moves to the remote stay local until released.</item>
/// <item>Keys pressed while controlling the remote are released on the remote when control comes
/// back, and their late physical releases are swallowed locally.</item>
/// </list>
/// Hooks can miss events (e.g. the mouse hook is removed while a game has focus), so the
/// remembered state is never trusted blindly: local state is re-checked against Windows, and
/// swallowed keys expire when auto-repeat shows they are no longer held.
/// </summary>
public sealed class CaptureRouter
{
    /// <summary>Held keys auto-repeat at least every second (Windows' longest repeat delay).</summary>
    private const long KeyHeldTimeoutMs = 1200;

    /// <summary>Mouse buttons don't auto-repeat; give up on a missed release after this long.</summary>
    private const long ButtonHeldTimeoutMs = 15_000;

    private readonly Dictionary<KeyId, ushort> _localHeldKeys = new();
    private readonly HashSet<MouseButton> _localHeldButtons = new();
    private readonly Dictionary<KeyId, long> _foreignKeys = new();
    private readonly Dictionary<MouseButton, long> _foreignButtons = new();

    /// <summary>Monotonic milliseconds; replaceable for tests.</summary>
    public Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>What the remote computer currently has pressed because of us.</summary>
    public PressedInputTracker RemotePressed { get; } = new();

    public bool Capturing { get; private set; }

    /// <summary>
    /// True while some key or button that was pressed remotely (or swallowed) is still physically
    /// held. The platform layer keeps its hooks installed until this becomes false, so the
    /// release gets swallowed instead of confusing the local computer.
    /// </summary>
    public bool HasPendingForeignReleases => _foreignKeys.Count > 0 || _foreignButtons.Count > 0;

    public bool AnyLocalButtonHeld => _localHeldButtons.Count > 0;

    /// <summary>Starts forwarding. <paramref name="isDown"/> checks a virtual key's real state.</summary>
    public void BeginCapture(Func<ushort, bool>? isDown = null)
    {
        if (isDown is not null)
        {
            PruneLocal(isDown);
        }
        Capturing = true;
    }

    /// <summary>Stops forwarding. Returns the releases the remote needs so nothing stays held there.</summary>
    public List<InputEvent> EndCapture()
    {
        Capturing = false;
        return RemotePressed.ReleaseAll();
    }

    /// <summary>Forgets locally held keys/buttons that Windows says are not actually down.</summary>
    public void PruneLocal(Func<ushort, bool> isDown)
    {
        List<KeyId>? staleKeys = null;
        foreach (var (id, vk) in _localHeldKeys)
        {
            if (vk != 0 && !isDown(vk))
            {
                (staleKeys ??= new()).Add(id);
            }
        }
        if (staleKeys is not null)
        {
            foreach (var id in staleKeys)
            {
                _localHeldKeys.Remove(id);
            }
        }
        _localHeldButtons.RemoveWhere(b => !isDown(ButtonVirtualKey(b)));
    }

    /// <summary>Drops swallowed keys/buttons whose release was evidently missed.</summary>
    public void ExpireStale()
    {
        var now = Clock();
        List<KeyId>? keys = null;
        foreach (var (id, last) in _foreignKeys)
        {
            if (now - last > KeyHeldTimeoutMs && !RemotePressed.IsPressed(id))
            {
                (keys ??= new()).Add(id);
            }
        }
        if (keys is not null)
        {
            foreach (var id in keys)
            {
                _foreignKeys.Remove(id);
            }
        }
        List<MouseButton>? buttons = null;
        foreach (var (button, last) in _foreignButtons)
        {
            if (now - last > ButtonHeldTimeoutMs && !RemotePressed.IsPressed(button))
            {
                (buttons ??= new()).Add(button);
            }
        }
        if (buttons is not null)
        {
            foreach (var button in buttons)
            {
                _foreignButtons.Remove(button);
            }
        }
    }

    /// <summary>Marks a key press as consumed (e.g. the hotkey) so its release is swallowed too.</summary>
    public void SwallowUntilReleased(in InputEvent e)
    {
        if (e.Kind == InputEventKind.Key)
        {
            _foreignKeys[e.KeyId] = Clock();
            _localHeldKeys.Remove(e.KeyId);
        }
        else if (e.Kind == InputEventKind.MouseButton)
        {
            _foreignButtons[e.Button] = Clock();
            _localHeldButtons.Remove(e.Button);
        }
    }

    public InputRoute Route(in InputEvent e) => e.Kind switch
    {
        InputEventKind.Key => RouteKey(e),
        InputEventKind.MouseButton => RouteButton(e),
        _ => Capturing ? InputRoute.Remote : InputRoute.Local,
    };

    private InputRoute RouteKey(in InputEvent e)
    {
        var id = e.KeyId;

        if (_localHeldKeys.ContainsKey(id))
        {
            // Pressed locally before capture started (or while not capturing): stays local.
            if (!e.Down)
            {
                _localHeldKeys.Remove(id);
            }
            return InputRoute.Local;
        }

        if (_foreignKeys.TryGetValue(id, out var lastEvent))
        {
            var now = Clock();
            if (Capturing && RemotePressed.IsPressed(id))
            {
                // Auto-repeat or release of a key held on the remote.
                RemotePressed.Observe(e);
                if (e.Down)
                {
                    _foreignKeys[id] = now;
                }
                else
                {
                    _foreignKeys.Remove(id);
                }
                return InputRoute.Remote;
            }

            if (e.Down && now - lastEvent > KeyHeldTimeoutMs)
            {
                // Too long since the last auto-repeat: its release was missed and this is a
                // fresh press. Route it normally.
                _foreignKeys.Remove(id);
            }
            else
            {
                // Press went elsewhere (hotkey, or the remote was already released): swallow the rest.
                if (e.Down)
                {
                    _foreignKeys[id] = now;
                }
                else
                {
                    _foreignKeys.Remove(id);
                }
                return InputRoute.Swallow;
            }
        }

        if (!Capturing)
        {
            if (e.Down)
            {
                _localHeldKeys[id] = e.VirtualKey;
            }
            return InputRoute.Local;
        }

        if (e.Down)
        {
            _foreignKeys[id] = Clock();
            RemotePressed.Observe(e);
            return InputRoute.Remote;
        }

        // Release of a key we never saw pressed: forwarding it is harmless and keeps the
        // remote consistent if it had it pressed for any reason.
        return InputRoute.Remote;
    }

    private InputRoute RouteButton(in InputEvent e)
    {
        var button = e.Button;

        if (_localHeldButtons.Contains(button))
        {
            if (!e.Down)
            {
                _localHeldButtons.Remove(button);
            }
            return InputRoute.Local;
        }

        if (_foreignButtons.ContainsKey(button))
        {
            if (Capturing && RemotePressed.IsPressed(button))
            {
                RemotePressed.Observe(e);
                if (!e.Down)
                {
                    _foreignButtons.Remove(button);
                }
                return InputRoute.Remote;
            }

            _foreignButtons.Remove(button);
            if (!e.Down)
            {
                return InputRoute.Swallow; // the late release of a press that went elsewhere
            }
            // Buttons don't auto-repeat, so a second press means the release was missed:
            // treat it as a fresh press below.
        }

        if (!Capturing)
        {
            if (e.Down)
            {
                _localHeldButtons.Add(button);
            }
            return InputRoute.Local;
        }

        if (e.Down)
        {
            _foreignButtons[button] = Clock();
            RemotePressed.Observe(e);
        }
        return InputRoute.Remote;
    }

    /// <summary>Windows virtual-key code of a mouse button (for GetAsyncKeyState).</summary>
    public static ushort ButtonVirtualKey(MouseButton button) => button switch
    {
        MouseButton.Left => 0x01,
        MouseButton.Right => 0x02,
        MouseButton.Middle => 0x04,
        MouseButton.X1 => 0x05,
        _ => 0x06,
    };
}

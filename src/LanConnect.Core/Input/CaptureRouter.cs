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
/// </summary>
public sealed class CaptureRouter
{
    private readonly HashSet<KeyId> _localHeldKeys = new();
    private readonly HashSet<MouseButton> _localHeldButtons = new();
    private readonly HashSet<KeyId> _foreignKeys = new();
    private readonly HashSet<MouseButton> _foreignButtons = new();

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

    public void BeginCapture() => Capturing = true;

    /// <summary>Stops forwarding. Returns the releases the remote needs so nothing stays held there.</summary>
    public List<InputEvent> EndCapture()
    {
        Capturing = false;
        return RemotePressed.ReleaseAll();
    }

    /// <summary>Marks a key press as consumed (e.g. the hotkey) so its release is swallowed too.</summary>
    public void SwallowUntilReleased(in InputEvent e)
    {
        if (e.Kind == InputEventKind.Key)
        {
            _foreignKeys.Add(e.KeyId);
            _localHeldKeys.Remove(e.KeyId);
        }
        else if (e.Kind == InputEventKind.MouseButton)
        {
            _foreignButtons.Add(e.Button);
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

        if (_localHeldKeys.Contains(id))
        {
            // Pressed locally before capture started (or while not capturing): stays local.
            if (!e.Down)
            {
                _localHeldKeys.Remove(id);
            }
            return InputRoute.Local;
        }

        if (_foreignKeys.Contains(id))
        {
            if (Capturing && RemotePressed.IsPressed(id))
            {
                // Auto-repeat or release of a key held on the remote.
                RemotePressed.Observe(e);
                if (!e.Down)
                {
                    _foreignKeys.Remove(id);
                }
                return InputRoute.Remote;
            }

            // Press went elsewhere (hotkey, or the remote was already released): swallow the rest.
            if (!e.Down)
            {
                _foreignKeys.Remove(id);
            }
            return InputRoute.Swallow;
        }

        if (!Capturing)
        {
            if (e.Down)
            {
                _localHeldKeys.Add(id);
            }
            return InputRoute.Local;
        }

        if (e.Down)
        {
            _foreignKeys.Add(id);
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

        if (_foreignButtons.Contains(button))
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

            if (!e.Down)
            {
                _foreignButtons.Remove(button);
            }
            return InputRoute.Swallow;
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
            _foreignButtons.Add(button);
            RemotePressed.Observe(e);
        }
        return InputRoute.Remote;
    }
}

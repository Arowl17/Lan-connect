namespace LanConnect.Core.Input;

/// <summary>
/// Remembers which keys and mouse buttons are currently held down (as seen by whoever feeds it)
/// so they can all be released at once. This is the core of stuck-key protection: whenever
/// control moves away or a connection drops, every held key gets an explicit release.
/// </summary>
public sealed class PressedInputTracker
{
    private readonly Dictionary<KeyId, InputEvent> _keys = new();
    private readonly HashSet<MouseButton> _buttons = new();

    public bool AnyPressed => _keys.Count > 0 || _buttons.Count > 0;

    public int PressedKeyCount => _keys.Count;

    public bool AnyButtonPressed => _buttons.Count > 0;

    public bool IsPressed(KeyId key) => _keys.ContainsKey(key);

    public bool IsPressed(MouseButton button) => _buttons.Contains(button);

    public void Observe(in InputEvent e)
    {
        switch (e.Kind)
        {
            case InputEventKind.Key:
                if (e.Down)
                {
                    _keys[e.KeyId] = e;
                }
                else
                {
                    _keys.Remove(e.KeyId);
                }
                break;
            case InputEventKind.MouseButton:
                if (e.Down)
                {
                    _buttons.Add(e.Button);
                }
                else
                {
                    _buttons.Remove(e.Button);
                }
                break;
        }
    }

    /// <summary>Returns release events for everything held, and forgets it.</summary>
    public List<InputEvent> ReleaseAll()
    {
        var releases = new List<InputEvent>(_keys.Count + _buttons.Count);
        foreach (var down in _keys.Values)
        {
            releases.Add(InputEvent.KeyEvent(down.ScanCode, down.VirtualKey, down.Extended, down: false));
        }
        foreach (var button in _buttons)
        {
            releases.Add(InputEvent.MouseButtonEvent(button, down: false));
        }
        _keys.Clear();
        _buttons.Clear();
        return releases;
    }

    public void Clear()
    {
        _keys.Clear();
        _buttons.Clear();
    }
}

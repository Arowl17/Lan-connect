using LanConnect.Core.Input;
using LanConnect.Core.Layout;
using LanConnect.Core.Switching;

namespace LanConnect.Core.Engine;

/// <summary>What the engine needs from the operating system. Implemented by the Windows layer.</summary>
public interface IPlatform
{
    /// <summary>Current monitor arrangement (physical pixels).</summary>
    DesktopLayout GetLayout();

    PointI GetCursorPosition();

    /// <summary>Snapshot of the focused app and cursor state, used by game mode.</summary>
    ForegroundState GetForegroundState();

    /// <summary>The focused window (the same id as <see cref="ForegroundState.Window"/>). Must be cheap.</summary>
    nint GetForegroundWindowHandle();

    /// <summary>
    /// Keeps the cursor inside <paramref name="area"/>, or releases Lan Connect's own lock with
    /// null (a lock set by another app is left alone). Called repeatedly with the same value.
    /// </summary>
    void ConfineCursor(RectI? area);

    /// <summary>Puts the cursor somewhere without it counting as mouse input (games don't see it).</summary>
    void PlaceCursor(PointI position);

    /// <summary>Moves the cursor as real mouse input would (so hover effects work).</summary>
    void MoveCursorTo(PointI position);

    /// <summary>Injects a key, mouse button or wheel event.</summary>
    void Inject(in InputEvent e);

    /// <summary>Whether Windows currently considers a virtual key (or mouse button) pressed.</summary>
    bool IsVirtualKeyDown(ushort virtualKey);

    /// <summary>Installs or removes the low-level mouse hook. Removed while gaming for zero overhead.</summary>
    void SetMouseHookEnabled(bool enabled);

    /// <summary>Called when this computer starts or stops controlling another one.</summary>
    void OnControllingChanged(bool controlling);
}

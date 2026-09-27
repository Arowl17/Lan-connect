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

    /// <summary>Moves the cursor as real mouse input would (so hover effects work).</summary>
    void MoveCursorTo(PointI position);

    /// <summary>Injects a key, mouse button or wheel event.</summary>
    void Inject(in InputEvent e);

    /// <summary>Installs or removes the low-level mouse hook. Removed while gaming for zero overhead.</summary>
    void SetMouseHookEnabled(bool enabled);

    /// <summary>Called when this computer starts or stops controlling another one.</summary>
    void OnControllingChanged(bool controlling);
}

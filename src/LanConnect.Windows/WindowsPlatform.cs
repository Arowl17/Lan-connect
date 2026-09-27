using System.Runtime.InteropServices;
using LanConnect.Core;
using LanConnect.Core.Engine;
using LanConnect.Core.Input;
using LanConnect.Core.Layout;
using LanConnect.Core.Switching;
using static LanConnect.Windows.NativeMethods;

namespace LanConnect.Windows;

/// <summary>Windows implementation of everything the engine needs from the OS.</summary>
public sealed unsafe class WindowsPlatform : IPlatform, IDisposable
{
    private readonly object _processCacheLock = new();
    private readonly Dictionary<uint, (string Name, long Expires)> _processNames = new();
    private HookThread? _hooks;
    private bool _wantMouseHook;
    private bool _highResolutionTimer;
    private PointI _lastCursor;

    /// <summary>Connects the hooks once the engine exists (the engine needs the platform first).</summary>
    public void AttachHooks(HookThread hooks)
    {
        _hooks = hooks;
        hooks.SetMouseHookEnabled(_wantMouseHook);
    }

    public DesktopLayout GetLayout()
    {
        var monitors = new List<RectI>();
        var handle = GCHandle.Alloc(monitors);
        try
        {
            EnumDisplayMonitors(0, 0, &CollectMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        if (monitors.Count == 0)
        {
            // Should not happen; fall back to the virtual screen.
            monitors.Add(RectI.FromSize(
                GetSystemMetrics(SM_XVIRTUALSCREEN),
                GetSystemMetrics(SM_YVIRTUALSCREEN),
                Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)),
                Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN))));
        }
        return new DesktopLayout(monitors);
    }

    [UnmanagedCallersOnly]
    private static int CollectMonitor(nint monitor, nint hdc, RECT* rect, nint data)
    {
        var list = (List<RectI>)GCHandle.FromIntPtr(data).Target!;
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (GetMonitorInfoW(monitor, ref info))
        {
            var r = info.rcMonitor;
            list.Add(new RectI(r.Left, r.Top, r.Right, r.Bottom));
        }
        return 1;
    }

    public PointI GetCursorPosition()
    {
        // Fails while the secure desktop (UAC prompt, lock screen) is active.
        if (GetCursorPos(out var p))
        {
            _lastCursor = new PointI(p.X, p.Y);
        }
        return _lastCursor;
    }

    public ForegroundState GetForegroundState()
    {
        var cursorVisible = true;
        var cursorInfo = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
        if (GetCursorInfo(ref cursorInfo))
        {
            // 0 = hidden by an app (e.g. a game in mouse-look). SUPPRESSED = touch/pen, still "visible".
            cursorVisible = cursorInfo.flags != 0;
        }

        var cursorClipped = false;
        if (GetClipCursor(out var clip))
        {
            var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            cursorClipped = clip.Left > vx || clip.Top > vy || clip.Right < vx + vw || clip.Bottom < vy + vh;
        }

        var window = GetForegroundWindow();
        if (window == 0 || window == GetShellWindow() || window == GetDesktopWindow())
        {
            return new ForegroundState(null, null, false, cursorVisible, cursorClipped);
        }

        GetWindowThreadProcessId(window, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId)
        {
            // Our own settings window: not a game. Reading our own window's title would also
            // send a message to our UI thread, which the hook thread must never wait for.
            return new ForegroundState(null, null, false, cursorVisible, cursorClipped);
        }
        var processName = GetProcessName(pid);

        var title = GetText(window, isClass: false);
        var className = GetText(window, isClass: true);
        var isDesktop = className is "Progman" or "WorkerW" or "Shell_TrayWnd";
        var fullscreen = !isDesktop && IsFullscreen(window);
        return new ForegroundState(processName, title, fullscreen, cursorVisible, cursorClipped);
    }

    private static bool IsFullscreen(nint window)
    {
        if (!GetWindowRect(window, out var rect))
        {
            return false;
        }
        var monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info))
        {
            return false;
        }
        var m = info.rcMonitor;
        return rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
    }

    private static string? GetText(nint window, bool isClass)
    {
        const int capacity = 256;
        char* buffer = stackalloc char[capacity];
        var length = isClass ? GetClassNameW(window, buffer, capacity) : GetWindowTextW(window, buffer, capacity);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    private string? GetProcessName(uint pid)
    {
        var now = Environment.TickCount64;
        lock (_processCacheLock)
        {
            if (_processNames.TryGetValue(pid, out var cached) && cached.Expires > now)
            {
                return cached.Name;
            }
        }

        string? name = null;
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process != 0)
        {
            try
            {
                const int capacity = 1024;
                char* buffer = stackalloc char[capacity];
                var size = (uint)capacity;
                if (QueryFullProcessImageNameW(process, 0, buffer, ref size))
                {
                    name = Path.GetFileName(new string(buffer, 0, (int)size));
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }

        lock (_processCacheLock)
        {
            if (_processNames.Count > 256)
            {
                _processNames.Clear();
            }
            _processNames[pid] = (name ?? string.Empty, now + 10_000);
        }
        return name;
    }

    public void MoveCursorTo(PointI position)
    {
        // Absolute movement over the whole virtual desktop, sent as real input so apps see
        // hover/move events. Coordinates are normalized to 0..65535, aimed at pixel centers.
        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        var vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        var nx = (int)Math.Clamp(Math.Round((position.X - vx + 0.5) * 65536.0 / vw), 0, 65535);
        var ny = (int)Math.Clamp(Math.Round((position.Y - vy + 0.5) * 65536.0 / vh), 0, 65535);

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dx = nx,
                    dy = ny,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                },
            },
        };
        if (SendInput(1, &input, sizeof(INPUT)) == 0)
        {
            // Blocked (e.g. an elevated window is focused): at least move the pointer.
            SetCursorPos(position.X, position.Y);
        }
    }

    public void Inject(in InputEvent e)
    {
        INPUT input;
        switch (e.Kind)
        {
            case InputEventKind.Key:
                input = KeyInput(e);
                break;
            case InputEventKind.MouseButton:
                input = new INPUT { type = INPUT_MOUSE, u = new INPUTUNION { mi = ButtonInput(e) } };
                break;
            case InputEventKind.MouseWheel:
                input = new INPUT
                {
                    type = INPUT_MOUSE,
                    u = new INPUTUNION
                    {
                        mi = new MOUSEINPUT
                        {
                            mouseData = unchecked((uint)e.WheelDelta),
                            dwFlags = e.Horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL,
                        },
                    },
                };
                break;
            default:
                return;
        }
        if (SendInput(1, &input, sizeof(INPUT)) == 0)
        {
            Log.Warn($"SendInput failed for {e} (error {Marshal.GetLastPInvokeError()}); an admin window may be focused");
        }
    }

    private static INPUT KeyInput(in InputEvent e)
    {
        var flags = e.Down ? 0u : KEYEVENTF_KEYUP;
        if (e.Extended)
        {
            flags |= KEYEVENTF_EXTENDEDKEY;
        }
        var ki = new KEYBDINPUT { wVk = e.VirtualKey, wScan = e.ScanCode };
        if (e.ScanCode is > 0 and <= 0xFF && !PreferVirtualKey(e.VirtualKey))
        {
            // Scan codes behave like the physical key, independent of layouts and modifiers.
            ki.wVk = 0;
            flags |= KEYEVENTF_SCANCODE;
        }
        ki.dwFlags = flags;
        return new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = ki } };
    }

    /// <summary>
    /// Injected button events go through the "swap mouse buttons" setting, so a logical left
    /// click must be injected as a right-button event on a left-handed setup.
    /// </summary>
    private static MouseButton LogicalToInjected(MouseButton button)
    {
        if (button is MouseButton.Left or MouseButton.Right && GetSystemMetrics(SM_SWAPBUTTON) != 0)
        {
            return button == MouseButton.Left ? MouseButton.Right : MouseButton.Left;
        }
        return button;
    }

    /// <summary>Keys whose scan codes are ambiguous or special; injecting the virtual key is more reliable.</summary>
    private static bool PreferVirtualKey(ushort vk) => vk switch
    {
        0x13 => true, // Pause
        0x90 => true, // Num Lock
        0x2C => true, // Print Screen
        >= 0xA6 and <= 0xB7 => true, // browser, volume and media keys
        _ => false,
    };

    public bool IsVirtualKeyDown(ushort virtualKey)
    {
        // GetAsyncKeyState reports physical mouse buttons; our events use logical buttons.
        if (virtualKey is 0x01 or 0x02 && GetSystemMetrics(SM_SWAPBUTTON) != 0)
        {
            virtualKey ^= 0x03;
        }
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static MOUSEINPUT ButtonInput(in InputEvent e) => LogicalToInjected(e.Button) switch
    {
        MouseButton.Left => new MOUSEINPUT { dwFlags = e.Down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP },
        MouseButton.Right => new MOUSEINPUT { dwFlags = e.Down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP },
        MouseButton.Middle => new MOUSEINPUT { dwFlags = e.Down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP },
        MouseButton.X1 => new MOUSEINPUT { dwFlags = e.Down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, mouseData = XBUTTON1 },
        _ => new MOUSEINPUT { dwFlags = e.Down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, mouseData = XBUTTON2 },
    };

    public void SetMouseHookEnabled(bool enabled)
    {
        _wantMouseHook = enabled;
        _hooks?.SetMouseHookEnabled(enabled);
    }

    public void OnControllingChanged(bool controlling)
    {
        // 1 ms timer resolution while controlling keeps the network sender's pacing tight.
        lock (_processCacheLock)
        {
            if (controlling && !_highResolutionTimer)
            {
                timeBeginPeriod(1);
                _highResolutionTimer = true;
            }
            else if (!controlling && _highResolutionTimer)
            {
                timeEndPeriod(1);
                _highResolutionTimer = false;
            }
        }
    }

    public void Dispose() => OnControllingChanged(false);
}

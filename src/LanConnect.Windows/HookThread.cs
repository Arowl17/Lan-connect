using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LanConnect.Core;
using LanConnect.Core.Engine;
using LanConnect.Core.Input;
using LanConnect.Core.Layout;
using LanConnect.Core.Net;
using static LanConnect.Windows.NativeMethods;

namespace LanConnect.Windows;

/// <summary>
/// Runs the low-level keyboard and mouse hooks on a dedicated high-priority thread with its own
/// message loop. The callbacks do almost nothing: they ask the engine "swallow or pass?" and
/// return, because Windows waits for them before delivering input to any application.
///
/// The keyboard hook is always installed (hotkeys, stuck-key protection). The mouse hook is only
/// installed while it can matter; during games it is removed so the game gets untouched input.
/// </summary>
public sealed unsafe class HookThread : IDisposable
{
    private const uint MsgEnableMouseHook = WM_APP + 1;
    private const uint MsgDisableMouseHook = WM_APP + 2;

    private static SwitchEngine? s_engine;
    private static nint s_keyboardHook;
    private static nint s_mouseHook;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private uint _threadId;
    private volatile bool _wantMouseHook;

    public HookThread(SwitchEngine engine)
    {
        s_engine = engine;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "LanConnect input hooks",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
    }

    public bool KeyboardHookInstalled => s_keyboardHook != 0;

    public void SetMouseHookEnabled(bool enabled)
    {
        _wantMouseHook = enabled;
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, enabled ? MsgEnableMouseHook : MsgDisableMouseHook, 0, 0);
        }
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var module = GetModuleHandleW(null);
        s_keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, &KeyboardProc, module, 0);
        if (s_keyboardHook == 0)
        {
            Log.Error($"Keyboard hook failed (error {Marshal.GetLastPInvokeError()})");
        }
        if (_wantMouseHook)
        {
            InstallMouseHook(module);
        }
        _started.Set();

        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            switch (msg.message)
            {
                case MsgEnableMouseHook:
                    if (_wantMouseHook)
                    {
                        InstallMouseHook(module);
                    }
                    break;
                case MsgDisableMouseHook:
                    if (!_wantMouseHook)
                    {
                        RemoveMouseHook();
                    }
                    break;
            }
        }

        RemoveMouseHook();
        if (s_keyboardHook != 0)
        {
            UnhookWindowsHookEx(s_keyboardHook);
            s_keyboardHook = 0;
        }
    }

    private static void InstallMouseHook(nint module)
    {
        if (s_mouseHook != 0)
        {
            return;
        }
        s_mouseHook = SetWindowsHookExW(WH_MOUSE_LL, &MouseProc, module, 0);
        if (s_mouseHook == 0)
        {
            Log.Error($"Mouse hook failed (error {Marshal.GetLastPInvokeError()})");
        }
    }

    private static void RemoveMouseHook()
    {
        if (s_mouseHook != 0)
        {
            UnhookWindowsHookEx(s_mouseHook);
            s_mouseHook = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint MouseProc(int nCode, nuint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0 && s_engine is { } engine)
            {
                var info = (MSLLHOOKSTRUCT*)lParam;
                // Our own injected input (and other tools') passes straight through.
                if ((info->flags & LLMHF_INJECTED) == 0 && HandleMouse(engine, (int)wParam, info))
                {
                    return 1;
                }
            }
        }
        catch (Exception e)
        {
            Log.Error("Mouse hook error", e);
        }
        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    private static bool HandleMouse(SwitchEngine engine, int message, MSLLHOOKSTRUCT* info)
    {
        switch (message)
        {
            case WM_MOUSEMOVE:
                return engine.HandleMouseMove(new PointI(info->pt.X, info->pt.Y), PeerLink.NowMs);
            case WM_LBUTTONDOWN:
                return engine.HandleMouseEvent(InputEvent.MouseButtonEvent(MouseButton.Left, true));
            case WM_LBUTTONUP:
                return engine.HandleMouseEvent(InputEvent.MouseButtonEvent(MouseButton.Left, false));
            case WM_RBUTTONDOWN:
                return engine.HandleMouseEvent(InputEvent.MouseButtonEvent(MouseButton.Right, true));
            case WM_RBUTTONUP:
                return engine.HandleMouseEvent(InputEvent.MouseButtonEvent(MouseButton.Right, false));
            case WM_MBUTTONDOWN:
                return engine.HandleMouseEvent(InputEvent.MouseButtonEvent(MouseButton.Middle, true));
            case WM_MBUTTONUP:
                return engine.HandleMouseEvent(InputEvent.MouseButtonEvent(MouseButton.Middle, false));
            case WM_XBUTTONDOWN:
            case WM_XBUTTONUP:
                {
                    var which = (info->mouseData >> 16) == XBUTTON2 ? MouseButton.X2 : MouseButton.X1;
                    return engine.HandleMouseEvent(InputEvent.MouseButtonEvent(which, message == WM_XBUTTONDOWN));
                }
            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                {
                    var delta = (short)(info->mouseData >> 16);
                    return engine.HandleMouseEvent(InputEvent.Wheel(delta, message == WM_MOUSEHWHEEL));
                }
            default:
                return false;
        }
    }

    [UnmanagedCallersOnly]
    private static nint KeyboardProc(int nCode, nuint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0 && s_engine is { } engine)
            {
                var info = (KBDLLHOOKSTRUCT*)lParam;
                if ((info->flags & LLKHF_INJECTED) == 0)
                {
                    var message = (int)wParam;
                    var down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                    var e = InputEvent.KeyEvent(
                        (ushort)info->scanCode,
                        (ushort)info->vkCode,
                        (info->flags & LLKHF_EXTENDED) != 0,
                        down);
                    if (engine.HandleKey(e))
                    {
                        return 1;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Error("Keyboard hook error", e);
        }
        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
            _thread.Join(2000);
        }
        s_engine = null;
        _started.Dispose();
    }
}

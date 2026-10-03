using System.Collections.Concurrent;
using LanConnect.Core.Engine;
using LanConnect.Core.Input;
using LanConnect.Core.Layout;
using LanConnect.Core.Net;
using LanConnect.Core.Settings;
using LanConnect.Core.Switching;

namespace LanConnect.Core.Tests;

internal sealed class FakePlatform(DesktopLayout layout) : IPlatform
{
    private readonly object _lock = new();
    private PointI _cursor = new(100, 100);

    public DesktopLayout Layout { get; set; } = layout;

    public ForegroundState Foreground { get; set; } = ForegroundState.Desktop;

    public ConcurrentQueue<InputEvent> Injected { get; } = new();

    public bool MouseHookEnabled { get; private set; }

    public bool Controlling { get; private set; }

    public PointI Cursor
    {
        get { lock (_lock) return _cursor; }
        set { lock (_lock) _cursor = value; }
    }

    public DesktopLayout GetLayout() => Layout;

    public PointI GetCursorPosition() => Cursor;

    public ForegroundState GetForegroundState() => Foreground;

    public nint GetForegroundWindowHandle() => Foreground.Window;

    /// <summary>Where Lan Connect keeps the cursor (null = free).</summary>
    public RectI? Confined { get; private set; }

    public void ConfineCursor(RectI? area)
    {
        Confined = area;
        Cursor = KeepInside(Cursor);
    }

    /// <summary>Like Windows, moves the cursor into the confinement area.</summary>
    public PointI KeepInside(PointI p) => Confined is { } a
        ? new PointI(Math.Clamp(p.X, a.Left, a.Right - 1), Math.Clamp(p.Y, a.Top, a.Bottom - 1))
        : p;

    public PointI? LastPlaced { get; private set; }

    public void PlaceCursor(PointI position)
    {
        LastPlaced = position;
        Cursor = KeepInside(position);
    }

    /// <summary>Simulates Windows applying injected moves late: the cursor reads one move behind.</summary>
    public bool LagMoves { get; set; }

    public PointI? LastMoveTarget { get; private set; }

    private PointI? _laggedMove;

    public void MoveCursorTo(PointI position)
    {
        LastMoveTarget = position;
        if (!LagMoves)
        {
            Cursor = position;
            return;
        }
        if (_laggedMove is { } previous)
        {
            Cursor = previous;
        }
        _laggedMove = position;
    }

    public void Inject(in InputEvent e) => Injected.Enqueue(e);

    /// <summary>Real key state as Windows would report it (default: trust the hooks).</summary>
    public Func<ushort, bool> KeyDown { get; set; } = _ => true;

    public bool IsVirtualKeyDown(ushort virtualKey) => KeyDown(virtualKey);

    public void SetMouseHookEnabled(bool enabled) => MouseHookEnabled = enabled;

    public void OnControllingChanged(bool controlling) => Controlling = controlling;
}

internal sealed class TestComputer : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanconnect-tests", Guid.NewGuid().ToString("N"));

    public TestComputer(string name, DesktopLayout layout)
    {
        Identity = DeviceIdentity.Create();
        Settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        Settings.Update(s => s.DeviceName = name);
        Platform = new FakePlatform(layout);
        Host = new NetworkHost(Identity, () => name, Settings, "test", tcpPort: 0, udpPort: 0);
        Engine = new SwitchEngine(Platform, Settings, Host, discovery: null);
        Clipboard = new FakeClipboard();
        ClipboardSync = new Clipboard.ClipboardSync(Clipboard, () => Settings.Snapshot().Clipboard, Path.Combine(_dir, "clipboard"));
        Host.PeerConnected += ClipboardSync.Attach;
        Engine.ControlLeaving += ClipboardSync.OnControlLeaving;
        Host.Start();
    }

    public FakeClipboard Clipboard { get; }

    public Clipboard.ClipboardSync ClipboardSync { get; }

    public string Directory => _dir;

    public DeviceIdentity Identity { get; }

    public SettingsStore Settings { get; }

    public FakePlatform Platform { get; }

    public NetworkHost Host { get; }

    public SwitchEngine Engine { get; }

    public string Id => Identity.Fingerprint;

    /// <summary>Simulates Windows moving the cursor unless the engine swallows the movement.</summary>
    public bool Move(int dx, int dy)
    {
        var from = Platform.Cursor;
        var proposed = new PointI(from.X + dx, from.Y + dy);
        var swallowed = Engine.HandleMouseMove(proposed, PeerLink.NowMs);
        if (!swallowed)
        {
            Platform.Cursor = Platform.KeepInside(Platform.Layout.Clamp(proposed));
        }
        return swallowed;
    }

    public bool Key(ushort vk, bool down) => Engine.HandleKey(InputEvent.KeyEvent((ushort)(vk + 0x100), vk, false, down));

    public async ValueTask DisposeAsync()
    {
        Engine.Dispose();
        await Host.DisposeAsync();
        Identity.Dispose();
        try
        {
            System.IO.Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class EngineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static DesktopLayout PcLayout() => new([RectI.FromSize(0, 0, 1920, 1080), RectI.FromSize(1920, 0, 1920, 1080)]);

    private static DesktopLayout LaptopLayout() => new([RectI.FromSize(0, 0, 1920, 1200)]);

    /// <summary>Gaming PC (2 monitors) with the work laptop to its right, paired and connected.</summary>
    internal static async Task<(TestComputer Pc, TestComputer Laptop)> SetUpAsync()
    {
        var pc = new TestComputer("Gaming PC", PcLayout());
        var laptop = new TestComputer("Work Laptop", LaptopLayout());
        pc.Settings.Update(s => s.Devices.Add(new PairedDevice { Id = laptop.Id, Name = "Work Laptop", Placement = Side.Right, LastAddress = "127.0.0.1", LastPort = laptop.Host.TcpPort }));
        laptop.Settings.Update(s => s.Devices.Add(new PairedDevice { Id = pc.Id, Name = "Gaming PC", Placement = Side.Left, LastAddress = "127.0.0.1", LastPort = pc.Host.TcpPort }));
        await WaitUntilAsync(() => pc.Engine.IsConnected(laptop.Id) && laptop.Engine.IsConnected(pc.Id));
        await WaitUntilAsync(() => pc.Platform.MouseHookEnabled); // edge switching armed
        return (pc, laptop);
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (!condition())
        {
            await Task.Delay(20, cts.Token);
        }
    }

    [Fact]
    public async Task FullRoundTripAcrossTheEdge()
    {
        var (pc, laptop) = await SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;

        // Flick to the right edge of the right monitor, mid-height.
        pc.Platform.Cursor = new PointI(3800, 540);
        Assert.True(pc.Move(150, 0));
        Assert.Equal(ControlState.Controlling, pc.Engine.Status.State);
        Assert.True(pc.Platform.MouseHookEnabled);

        // The cursor appears on the laptop's left edge at the same relative height.
        await WaitUntilAsync(() => laptop.Engine.Status.State == ControlState.ControlledBy);
        await WaitUntilAsync(() => laptop.Platform.Cursor.X == 0);
        Assert.InRange(laptop.Platform.Cursor.Y, 595, 605);

        // Mouse movement on the PC moves the laptop's cursor; the PC's cursor stays put.
        var pcCursor = pc.Platform.Cursor;
        Assert.True(pc.Move(100, 50));
        await WaitUntilAsync(() => laptop.Platform.Cursor == new PointI(100, laptop.Platform.Cursor.Y) && laptop.Platform.Cursor.Y >= 645);
        Assert.Equal(pcCursor, pc.Platform.Cursor);

        // Typing goes to the laptop, not the PC.
        Assert.True(pc.Key(0x41, true));
        Assert.True(pc.Key(0x41, false));
        await WaitUntilAsync(() => laptop.Platform.Injected.Count >= 2);
        Assert.Collection(laptop.Platform.Injected,
            e => Assert.True(e.Kind == InputEventKind.Key && e.VirtualKey == 0x41 && e.Down),
            e => Assert.True(e.Kind == InputEventKind.Key && e.VirtualKey == 0x41 && !e.Down));

        // Push back through the laptop's left edge: control returns to the PC.
        for (var i = 0; i < 10 && pc.Engine.Status.State == ControlState.Controlling; i++)
        {
            pc.Move(-80, 0);
            await Task.Delay(30);
        }
        await WaitUntilAsync(() => pc.Engine.Status.State == ControlState.Local);
        await WaitUntilAsync(() => laptop.Engine.Status.State == ControlState.Local);
        // The PC cursor is placed just inside its right edge. (Checking where the engine put it,
        // not where it is now: a push from the loop above can land after control came back and
        // legitimately move it further.)
        await WaitUntilAsync(() => pc.Platform.LastMoveTarget is { X: 3838 });
        Assert.False(pc.Move(-10, 0)); // local again
    }

    [Fact]
    public async Task HotkeySwitchesAndReleasesKeysHeldOnTheLaptop()
    {
        var (pc, laptop) = await SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;

        // The test holds B without auto-repeat events; freeze the router's clock so the
        // "release was missed" expiry can't kick in on a slow machine.
        pc.Engine.Router.Clock = () => 0;

        // Ctrl+Alt+Right: go to the laptop. Ctrl and Alt reach the PC as usual.
        Assert.False(pc.Key(VirtualKeys.LControl, true));
        Assert.False(pc.Key(VirtualKeys.LMenu, true));
        Assert.True(pc.Key(VirtualKeys.Right, true));
        Assert.Equal(ControlState.Controlling, pc.Engine.Status.State);
        Assert.True(pc.Key(VirtualKeys.Right, false)); // the hotkey's release is swallowed
        await WaitUntilAsync(() => laptop.Engine.Status.State == ControlState.ControlledBy);

        // Hold B on the laptop, then jump back with Ctrl+Alt+Left while B is still held.
        Assert.True(pc.Key(0x42, true));
        await WaitUntilAsync(() => laptop.Platform.Injected.Any(e => e.VirtualKey == 0x42 && e.Down));
        Assert.True(pc.Key(VirtualKeys.Left, true));
        Assert.Equal(ControlState.Local, pc.Engine.Status.State);

        // The laptop releases B by itself: no stuck key there.
        await WaitUntilAsync(() => laptop.Engine.Status.State == ControlState.Local);
        await WaitUntilAsync(() => laptop.Platform.Injected.Any(e => e.VirtualKey == 0x42 && !e.Down));

        // The late physical releases of B and Left never reach the PC; Ctrl and Alt do.
        Assert.True(pc.Key(0x42, false));
        Assert.True(pc.Key(VirtualKeys.Left, false));
        Assert.False(pc.Key(VirtualKeys.LControl, false));
        Assert.False(pc.Key(VirtualKeys.LMenu, false));

        // Typing works normally on the PC again.
        Assert.False(pc.Key(0x42, true));
        Assert.False(pc.Key(0x42, false));
    }

    [Fact]
    public async Task ExtraModifiersAreNotTheHotkey()
    {
        var (pc, laptop) = await SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;

        pc.Key(VirtualKeys.LControl, true);
        pc.Key(VirtualKeys.LMenu, true);
        pc.Key(VirtualKeys.LShift, true);
        Assert.False(pc.Key(VirtualKeys.Right, true)); // Ctrl+Alt+Shift+Right belongs to other apps
        Assert.Equal(ControlState.Local, pc.Engine.Status.State);
    }

    [Fact]
    public async Task GameModeBlocksEdgeSwitchingButNotTheHotkey()
    {
        var (pc, laptop) = await SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;

        pc.Platform.Foreground = new ForegroundState("GeForceNOW.exe", "GeForce NOW", false, true, false);
        pc.Platform.Cursor = new PointI(3800, 540);
        Assert.False(pc.Move(300, 0));
        Assert.Equal(ControlState.Local, pc.Engine.Status.State);

        // The mouse hook is removed entirely while the game is focused.
        await WaitUntilAsync(() => !pc.Platform.MouseHookEnabled);
        Assert.NotNull(pc.Engine.Status.GameModeReason);

        // The hotkey still works.
        pc.Key(VirtualKeys.LControl, true);
        pc.Key(VirtualKeys.LMenu, true);
        Assert.True(pc.Key(VirtualKeys.Right, true));
        Assert.Equal(ControlState.Controlling, pc.Engine.Status.State);
        Assert.True(pc.Platform.MouseHookEnabled || await Task.Run(async () =>
        {
            await WaitUntilAsync(() => pc.Platform.MouseHookEnabled);
            return true;
        }));
    }

    [Fact]
    public async Task CornersAndDraggingNeverSwitch()
    {
        var (pc, laptop) = await SetUpAsync();
        await using var _ = pc;
        await using var __ = laptop;

        pc.Platform.Cursor = new PointI(3830, 3);
        Assert.False(pc.Move(200, 0)); // top-right corner
        pc.Platform.Cursor = new PointI(3800, 540);
        Assert.False(pc.Engine.HandleMouseEvent(InputEvent.MouseButtonEvent(MouseButton.Left, true)));
        Assert.False(pc.Move(300, 0)); // dragging something
        Assert.False(pc.Engine.HandleMouseEvent(InputEvent.MouseButtonEvent(MouseButton.Left, false)));
        Assert.True(pc.Move(300, 0));
    }

    [Fact]
    public async Task LosingTheLaptopGivesTheMouseBack()
    {
        var (pc, laptop) = await SetUpAsync();
        await using var _ = pc;

        pc.Platform.Cursor = new PointI(3800, 540);
        Assert.True(pc.Move(150, 0));
        await WaitUntilAsync(() => laptop.Engine.Status.State == ControlState.ControlledBy);

        await laptop.DisposeAsync(); // laptop closes / crashes / leaves Wi-Fi
        await WaitUntilAsync(() => pc.Engine.Status.State == ControlState.Local);
        Assert.False(pc.Move(-10, 0));
    }
}

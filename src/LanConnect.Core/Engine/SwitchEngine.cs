using System.Net;
using LanConnect.Core.Input;
using LanConnect.Core.Layout;
using LanConnect.Core.Net;
using LanConnect.Core.Settings;
using LanConnect.Core.Switching;

namespace LanConnect.Core.Engine;

public enum ControlState
{
    /// <summary>Mouse and keyboard work on this computer.</summary>
    Local,

    /// <summary>This computer's mouse and keyboard drive another computer.</summary>
    Controlling,

    /// <summary>Another computer is driving this one.</summary>
    ControlledBy,
}

public sealed record EngineStatus(ControlState State, string? PeerName, string? GameModeReason, bool Paused);

/// <summary>
/// The brain of Lan Connect: decides when control moves between computers and routes input.
///
/// Hook callbacks (<see cref="HandleMouseMove"/>, <see cref="HandleMouseEvent"/>,
/// <see cref="HandleKey"/>) run on the hook thread and must answer instantly whether to swallow
/// the event; network callbacks run on network threads. Shared state is guarded by one short lock,
/// and OS calls that inject input are made outside of it.
/// </summary>
public sealed class SwitchEngine : IDisposable
{
    private const int ControllerWatchdogMs = 1500;

    /// <summary>
    /// Only the computer with the smaller id dials right away; the other one waits this long
    /// first. If both dialed at once, one of the two connections would be dropped moments later
    /// (taking any control in progress with it).
    /// </summary>
    private const long SecondaryDialDelayMs = 6000;

    private readonly IPlatform _platform;
    private readonly SettingsStore _settings;
    private readonly NetworkHost _host;
    private readonly DiscoveryService? _discovery;
    private readonly object _lock = new();
    private readonly CaptureRouter _router = new();
    private readonly SwitchHotkeyDetector _hotkeys = new();
    private readonly EdgeSwitchPolicy _edgePolicy = new();
    private readonly EdgeSwitchPolicy _leavePolicy = new();
    private readonly PressedInputTracker _injected = new();
    private readonly Dictionary<Side, string> _placements = new();
    private readonly List<Side> _activeSides = new();
    private readonly Dictionary<string, long> _offlineSince = new();
    private readonly Dictionary<string, long> _reachableSince = new();
    private readonly Timer _timer;

    private AppSettings _config;
    private DesktopLayout _layout;
    private ControlState _state = ControlState.Local;
    private PeerLink? _peer;
    private Side _peerSide;
    private PointI _tracked;
    private string? _gameModeReason;
    private bool _paused;
    private bool _mouseHookEnabled;
    private long _lastDialMs;
    private long _controlStartedMs;
    private PointI? _pendingCursorMove;
    private Side[] _leaveSides = [Side.Left];

    // Where we recently asked Windows to put the cursor while being controlled. SendInput is
    // processed asynchronously, so GetCursorPos may still report one of these; only a position
    // matching none of them means someone moved the cursor locally (e.g. the laptop's touchpad).
    private readonly PointI[] _recentTargets = new PointI[8];
    private int _recentTargetCount;
    private int _recentTargetNext;
    private readonly Func<ushort, bool> _isKeyDown;

    [ThreadStatic]
    private static List<Output>? t_outputs;

    public SwitchEngine(IPlatform platform, SettingsStore settings, NetworkHost host, DiscoveryService? discovery)
    {
        _platform = platform;
        _isKeyDown = platform.IsVirtualKeyDown;
        _settings = settings;
        _host = host;
        _discovery = discovery;
        _config = settings.Snapshot();
        _layout = platform.GetLayout();
        ApplyConfig();

        _settings.Changed += OnSettingsChanged;
        _host.PeerConnected += OnPeerConnected;
        _host.PeerDisconnected += OnPeerDisconnected;
        _timer = new Timer(_ => Tick(), null, 250, 250);
    }

    /// <summary>Tests: access to the router (e.g. to control its clock).</summary>
    internal CaptureRouter Router => _router;

    public event Action<EngineStatus>? StatusChanged;

    /// <summary>Raised when paired devices connect/disconnect or change.</summary>
    public event Action? PeersChanged;

    /// <summary>
    /// Control is moving from this computer to the given one (raised on a thread-pool thread).
    /// Used to bring the clipboard along.
    /// </summary>
    public event Action<PeerLink>? ControlLeaving;

    public EngineStatus Status
    {
        get
        {
            lock (_lock)
            {
                return new EngineStatus(_state, _peer?.PeerName, _gameModeReason, _paused);
            }
        }
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            lock (_lock)
            {
                _paused = value;
                if (value && _state == ControlState.Controlling)
                {
                    EndControlLocked(takeBack: true, leaveFraction: null);
                }
            }
            UpdateMouseHook();
            RaiseStatus();
        }
    }

    public bool IsConnected(string deviceId) => _host.GetLink(deviceId) is not null;

    /// <summary>Moves control to a connected, placed device (e.g. from the tray menu).</summary>
    public bool SwitchTo(string deviceId)
    {
        lock (_lock)
        {
            if (_paused || _state == ControlState.ControlledBy)
            {
                return false;
            }
            var entry = _placements.FirstOrDefault(p => p.Value == deviceId);
            if (entry.Value is null || _host.GetLink(deviceId) is not { } link)
            {
                return false;
            }
            if (_state == ControlState.Controlling)
            {
                if (_peer == link)
                {
                    return true;
                }
                EndControlLocked(takeBack: true, leaveFraction: null);
            }
            BeginControlLocked(link, entry.Key, 0.5);
            return true;
        }
    }

    /// <summary>Takes control back to this computer.</summary>
    public void ReturnHome()
    {
        lock (_lock)
        {
            EndControlLocked(takeBack: true, leaveFraction: null);
        }
    }

    public PeerLink? GetLink(string deviceId) => _host.GetLink(deviceId);

    // ------------------------------------------------------------------ hook thread

    /// <summary>
    /// A mouse movement is about to happen: <paramref name="proposed"/> is where the cursor would
    /// go (can be off screen). Returns true to swallow it.
    /// </summary>
    public bool HandleMouseMove(PointI proposed, long nowMs)
    {
        lock (_lock)
        {
            switch (_state)
            {
                case ControlState.Controlling when _peer is { } peer:
                    {
                        var at = _platform.GetCursorPosition();
                        peer.AddMotion(proposed.X - at.X, proposed.Y - at.Y);
                        return true;
                    }
                case ControlState.Local when !_paused && _activeSides.Count > 0:
                    {
                        var from = _platform.GetCursorPosition();
                        if (!EdgeSwitchPolicy.TouchesActiveEdge(_layout, _activeSides, from, proposed))
                        {
                            _edgePolicy.Reset();
                            return false;
                        }
                        if (_router.AnyLocalButtonHeld)
                        {
                            _router.PruneLocal(_isKeyDown); // a missed release must not block switching
                        }
                        var blocked = _router.AnyLocalButtonHeld || IsGameBlockedNow();
                        var decision = _edgePolicy.Evaluate(_layout, _activeSides, from, proposed, nowMs, blocked);
                        if (decision is { } d && _placements.TryGetValue(d.Side, out var id) && _host.GetLink(id) is { } link)
                        {
                            BeginControlLocked(link, d.Side, d.Fraction);
                            return true;
                        }
                        return false;
                    }
                default:
                    return false;
            }
        }
    }

    /// <summary>Mouse button or wheel event. Returns true to swallow it.</summary>
    public bool HandleMouseEvent(in InputEvent e)
    {
        lock (_lock)
        {
            return RouteLocked(e);
        }
    }

    /// <summary>Keyboard event. Returns true to swallow it.</summary>
    public bool HandleKey(in InputEvent e)
    {
        lock (_lock)
        {
            if (_hotkeys.Feed(e) is { } direction && !_paused
                && (_state != ControlState.Local || _hotkeys.ConfirmModifiers(_isKeyDown)))
            {
                if (HandleHotkeyLocked(direction))
                {
                    _router.SwallowUntilReleased(e);
                    return true;
                }
            }
            return RouteLocked(e);
        }
    }

    private bool RouteLocked(in InputEvent e)
    {
        switch (_router.Route(e))
        {
            case InputRoute.Remote:
                _peer?.Send(InputItem.FromEvent(e));
                return true;
            case InputRoute.Swallow:
                return true;
            default:
                return false;
        }
    }

    private bool HandleHotkeyLocked(Side direction)
    {
        switch (_state)
        {
            case ControlState.Local:
                if (_placements.TryGetValue(direction, out var id) && _host.GetLink(id) is { } link)
                {
                    var cursor = _platform.GetCursorPosition();
                    var fraction = _layout.FractionAlong(direction, cursor);
                    BeginControlLocked(link, direction, fraction);
                    return true;
                }
                return false;
            case ControlState.Controlling when direction == _peerSide.Opposite():
                EndControlLocked(takeBack: true, leaveFraction: null);
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ control transitions

    private void BeginControlLocked(PeerLink link, Side side, double fraction)
    {
        _state = ControlState.Controlling;
        _peer = link;
        _peerSide = side;
        _controlStartedMs = PeerLink.NowMs;
        _edgePolicy.Reset();
        _router.BeginCapture(_isKeyDown);
        link.KeepAwake = true;
        // The cursor enters the other computer through the edge facing us.
        link.Send(InputItem.Enter(side.Opposite(), fraction));
        RaiseControlLeaving(link);
        Log.Info($"Controlling {link.PeerName}");
        AfterStateChange(controlling: true);
    }

    /// <param name="takeBack">Tell the other computer we took control back (hotkey, timeout).</param>
    /// <param name="leaveFraction">When the cursor came back across the edge: where along it.</param>
    private void EndControlLocked(bool takeBack, double? leaveFraction)
    {
        if (_state != ControlState.Controlling)
        {
            return;
        }
        var peer = _peer;
        foreach (var release in _router.EndCapture())
        {
            peer?.Send(InputItem.FromEvent(release));
        }
        if (takeBack)
        {
            peer?.Send(InputItem.TakeBack());
        }
        if (peer is not null)
        {
            peer.KeepAwake = false;
        }
        _state = ControlState.Local;
        _peer = null;
        _edgePolicy.Reset();
        if (leaveFraction is { } f)
        {
            // Continue exactly where the cursor left the other screen.
            var entry = _layout.EntryPoint(_peerSide, f);
            var inward = _peerSide switch
            {
                Side.Left => new PointI(entry.X + 1, entry.Y),
                Side.Right => new PointI(entry.X - 1, entry.Y),
                Side.Top => new PointI(entry.X, entry.Y + 1),
                _ => new PointI(entry.X, entry.Y - 1),
            };
            _pendingCursorMove = _layout.Clamp(inward);
        }
        Log.Info($"Back to this computer{(peer is null ? "" : $" (from {peer.PeerName})")}");
        AfterStateChange(controlling: false);
    }

    private void AfterStateChange(bool controlling)
    {
        var move = _pendingCursorMove;
        _pendingCursorMove = null;
        // Runs under the lock, possibly on the hook thread: anything that injects input or waits
        // on the hook thread is done on the thread pool instead.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            _platform.OnControllingChanged(controlling);
            if (move is { } m)
            {
                _platform.MoveCursorTo(m);
            }
            UpdateMouseHook();
            RaiseStatus();
        });
    }

    // ------------------------------------------------------------------ network threads

    /// <summary>An OS call decided under the lock and performed after releasing it.</summary>
    private readonly record struct Output(bool IsMove, PointI Position, InputEvent Event)
    {
        public static Output Move(PointI position) => new(true, position, default);

        public static Output Inject(in InputEvent e) => new(false, default, e);
    }

    private static List<Output> RentOutputs()
    {
        var list = t_outputs ??= new List<Output>(64);
        list.Clear();
        return list;
    }

    private void Perform(List<Output> outputs)
    {
        foreach (var output in outputs)
        {
            if (output.IsMove)
            {
                _platform.MoveCursorTo(output.Position);
            }
            else
            {
                _platform.Inject(output.Event);
            }
        }
        outputs.Clear();
    }

    private void OnInputReceived(PeerLink link, IReadOnlyList<ReceivedInput> inputs)
    {
        var outputs = RentOutputs();
        var statusChanged = false;
        lock (_lock)
        {
            foreach (var input in inputs)
            {
                if (input.IsMotion)
                {
                    if (_state == ControlState.ControlledBy && _peer == link)
                    {
                        MoveControlledCursorLocked(link, input.Dx, input.Dy, outputs, ref statusChanged);
                    }
                    continue;
                }

                var item = input.Item;
                switch (item.Type)
                {
                    case InputItemType.Enter:
                        if (_state == ControlState.Controlling)
                        {
                            EndControlLocked(takeBack: true, leaveFraction: null);
                        }
                        if (_state == ControlState.ControlledBy && _peer != link)
                        {
                            ReleaseInjectedLocked(outputs);
                        }
                        _state = ControlState.ControlledBy;
                        _peer = link;
                        _peerSide = item.Side;
                        _controlStartedMs = PeerLink.NowMs;
                        _leaveSides = [item.Side];
                        _leavePolicy.Reset();
                        link.KeepAwake = true;
                        _recentTargetCount = 0;
                        // Until Windows applies the move, GetCursorPos still reports the old spot.
                        RememberTarget(_platform.GetCursorPosition());
                        _tracked = _layout.EntryPoint(item.Side, item.Fraction);
                        MoveCursorLocked(_tracked, outputs);
                        statusChanged = true;
                        Log.Info($"{link.PeerName} is controlling this computer");
                        break;

                    case InputItemType.Leave:
                        if (_state == ControlState.Controlling && _peer == link)
                        {
                            EndControlLocked(takeBack: false, leaveFraction: item.Fraction);
                        }
                        break;

                    case InputItemType.TakeBack:
                        if (_state == ControlState.ControlledBy && _peer == link)
                        {
                            RaiseControlLeaving(link);
                            ReleaseInjectedLocked(outputs);
                            link.KeepAwake = false;
                            _state = ControlState.Local;
                            _peer = null;
                            statusChanged = true;
                        }
                        break;

                    default:
                        if (_state == ControlState.ControlledBy && _peer == link)
                        {
                            var e = item.Event;
                            _injected.Observe(e);
                            outputs.Add(Output.Inject(e));
                        }
                        break;
                }
            }
        }

        Perform(outputs);
        if (statusChanged)
        {
            UpdateMouseHook();
            RaiseStatus();
        }
    }

    private void MoveControlledCursorLocked(PeerLink link, int dx, int dy, List<Output> outputs, ref bool statusChanged)
    {
        // Someone may have used this computer's own touchpad meanwhile: follow the real cursor,
        // unless it is just lagging behind moves we injected ourselves.
        var actual = _platform.GetCursorPosition();
        if (!IsNear(actual, _tracked) && !IsRecentTarget(actual))
        {
            _tracked = actual;
            _recentTargetCount = 0;
        }

        var proposed = new PointI(_tracked.X + dx, _tracked.Y + dy);
        var decision = _leavePolicy.Evaluate(_layout, _leaveSides, _tracked, proposed, PeerLink.NowMs, blocked: _injected.AnyButtonPressed);
        if (decision is { } d)
        {
            // Back to the controlling computer through the edge facing it.
            link.Send(InputItem.Leave(d.Side, d.Fraction));
            RaiseControlLeaving(link);
            ReleaseInjectedLocked(outputs);
            link.KeepAwake = false;
            _state = ControlState.Local;
            _peer = null;
            statusChanged = true;
            return;
        }

        var next = _layout.Clamp(proposed);
        if (next != _tracked)
        {
            _tracked = next;
            MoveCursorLocked(next, outputs);
        }
    }

    private void MoveCursorLocked(PointI target, List<Output> outputs)
    {
        RememberTarget(target);
        outputs.Add(Output.Move(target));
    }

    private void RememberTarget(PointI target)
    {
        _recentTargets[_recentTargetNext] = target;
        _recentTargetNext = (_recentTargetNext + 1) % _recentTargets.Length;
        _recentTargetCount = Math.Min(_recentTargetCount + 1, _recentTargets.Length);
    }

    private bool IsRecentTarget(PointI p)
    {
        for (var i = 0; i < _recentTargetCount; i++)
        {
            if (IsNear(p, _recentTargets[i]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Within a pixel: absolute injection can round by one.</summary>
    private static bool IsNear(PointI a, PointI b) => Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1;

    private void ReleaseInjectedLocked(List<Output> outputs)
    {
        foreach (var release in _injected.ReleaseAll())
        {
            outputs.Add(Output.Inject(release));
        }
    }

    private void OnControlReceived(PeerLink link, ControlMessage message)
    {
        switch (message)
        {
            case PlacementMessage placement:
                _settings.Update(s =>
                {
                    var device = s.Devices.FirstOrDefault(d => d.Id == link.PeerId);
                    if (device is null || (!placement.Explicit && device.Placement is not null))
                    {
                        return;
                    }
                    var side = placement.YouAreOnMy?.Opposite();
                    if (side is { } taken)
                    {
                        foreach (var other in s.Devices.Where(d => d.Id != device.Id && d.Placement == taken))
                        {
                            other.Placement = null;
                        }
                    }
                    device.Placement = side;
                });
                break;

            case UnpairMessage:
                Log.Info($"{link.PeerName} removed this computer");
                _settings.Update(s => s.Devices.RemoveAll(d => d.Id == link.PeerId));
                _ = _host.DisconnectAsync(link.PeerId);
                break;
        }
    }

    private void OnPeerConnected(PeerLink link)
    {
        link.InputReceived += OnInputReceived;
        link.ControlReceived += OnControlReceived;
        var device = _config.Devices.FirstOrDefault(d => d.Id == link.PeerId);
        _settings.Update(s =>
        {
            var d = s.Devices.FirstOrDefault(x => x.Id == link.PeerId);
            if (d is not null)
            {
                d.LastAddress = link.PeerAddress.ToString();
                d.Name = link.PeerName;
            }
        });
        if (device?.Placement is { } side)
        {
            _ = SendSafeAsync(link, new PlacementMessage(side, Explicit: false));
        }
        RefreshActiveSides();
        UpdateMouseHook();
        PeersChanged?.Invoke();
        RaiseStatus();
    }

    private void OnPeerDisconnected(PeerLink link)
    {
        link.InputReceived -= OnInputReceived;
        link.ControlReceived -= OnControlReceived;
        var outputs = RentOutputs();
        lock (_lock)
        {
            if (_peer == link)
            {
                if (_state == ControlState.Controlling)
                {
                    Log.Warn($"Lost connection to {link.PeerName} while controlling it; taking control back");
                    EndControlLocked(takeBack: false, leaveFraction: null);
                }
                else if (_state == ControlState.ControlledBy)
                {
                    ReleaseInjectedLocked(outputs);
                    _state = ControlState.Local;
                    _peer = null;
                }
            }
        }
        Perform(outputs);
        RefreshActiveSides();
        UpdateMouseHook();
        PeersChanged?.Invoke();
        RaiseStatus();
    }

    // ------------------------------------------------------------------ periodic work

    private void Tick()
    {
        try
        {
            // Game mode: checked a few times a second so the mouse hook can be removed while gaming.
            string? reason = null;
            if (_config.GameMode.Enabled)
            {
                reason = GameModeRules.BlockReason(_platform.GetForegroundState(), _config.GameMode);
            }

            var statusChanged = false;
            var outputs = RentOutputs();
            lock (_lock)
            {
                _router.ExpireStale();
                if (reason != _gameModeReason)
                {
                    _gameModeReason = reason;
                    statusChanged = true;
                }

                // Watchdog: never leave the user without a mouse if the other computer goes quiet.
                // Silence counts from the switch at the earliest: an idle peer only sends a packet
                // every second, and needs a moment to answer (e.g. its Wi-Fi waking from power save).
                if (_state == ControlState.Controlling && _peer is { } peer && SilenceSinceSwitch(peer) > ControllerWatchdogMs)
                {
                    Log.Warn($"{peer.PeerName} stopped responding; taking control back");
                    EndControlLocked(takeBack: true, leaveFraction: null);
                }
                else if (_state == ControlState.ControlledBy && _peer is { } controller && SilenceSinceSwitch(controller) > ControllerWatchdogMs * 2)
                {
                    Log.Warn($"{controller.PeerName} went quiet; releasing held keys");
                    ReleaseInjectedLocked(outputs);
                    controller.KeepAwake = false;
                    _state = ControlState.Local;
                    _peer = null;
                    statusChanged = true;
                }
            }
            Perform(outputs);

            UpdateMouseHook();
            if (statusChanged)
            {
                RaiseStatus();
            }

            var now = PeerLink.NowMs;
            if (now - _lastDialMs > 2000)
            {
                _lastDialMs = now;
                DialPairedDevices();
            }
        }
        catch (Exception e)
        {
            Log.Error("Engine tick failed", e);
        }
    }

    private void DialPairedDevices()
    {
        var discovered = _discovery?.Devices ?? [];
        var now = PeerLink.NowMs;
        foreach (var device in _config.Devices)
        {
            lock (_offlineSince)
            {
                if (_host.GetLink(device.Id) is not null)
                {
                    _offlineSince.Remove(device.Id);
                    _reachableSince.Remove(device.Id);
                    continue;
                }
                if (!_offlineSince.TryGetValue(device.Id, out var since))
                {
                    _offlineSince[device.Id] = since = now;
                }
                // Measure the delay from when the other computer (re)appeared on the network: it
                // dials us as soon as it starts, and must get the first go even after being off
                // for hours.
                if (discovered.Any(d => d.Id == device.Id))
                {
                    if (!_reachableSince.TryGetValue(device.Id, out var reachable))
                    {
                        _reachableSince[device.Id] = reachable = now;
                    }
                    since = Math.Max(since, reachable);
                }
                else
                {
                    _reachableSince.Remove(device.Id);
                }
                var primaryDialer = string.CompareOrdinal(_host.OwnId, device.Id) < 0;
                if (!primaryDialer && now - since < SecondaryDialDelayMs)
                {
                    continue; // give the other computer the first go
                }
            }
            var candidates = new List<IPEndPoint>();
            foreach (var seen in discovered.Where(d => d.Id == device.Id))
            {
                candidates.Add(new IPEndPoint(seen.Address, seen.TcpPort));
            }
            if (device.LastAddress is { } last && IPAddress.TryParse(last, out var address))
            {
                candidates.Add(new IPEndPoint(address, device.LastPort ?? FramedConnection.DefaultPort));
            }
            if (candidates.Count > 0)
            {
                _host.EnsureConnected(device.Id, candidates);
            }
        }
    }

    private long SilenceSinceSwitch(PeerLink peer) => Math.Min(peer.SilenceMs, PeerLink.NowMs - _controlStartedMs);

    /// <summary>Checked at the moment of an edge crossing, so it is always current.</summary>
    private bool IsGameBlockedNow() =>
        _config.GameMode.Enabled && GameModeRules.BlockReason(_platform.GetForegroundState(), _config.GameMode) is not null;

    private void UpdateMouseHook()
    {
        lock (_lock)
        {
            var wanted = _state == ControlState.Controlling
                || _router.HasPendingForeignReleases
                || (_state == ControlState.Local && !_paused && _activeSides.Count > 0 && _gameModeReason is null);
            if (wanted == _mouseHookEnabled)
            {
                return;
            }
            _mouseHookEnabled = wanted;
            // Applied under the lock so concurrent callers can't apply stale decisions out of
            // order. The platform only posts a message to the hook thread, so this never blocks.
            _platform.SetMouseHookEnabled(wanted);
        }
    }

    // ------------------------------------------------------------------ configuration

    /// <summary>Places a paired device on a side of this computer and tells the device.</summary>
    public void SetPlacement(string deviceId, Side? side)
    {
        _settings.Update(s =>
        {
            foreach (var device in s.Devices)
            {
                if (device.Id == deviceId)
                {
                    device.Placement = side;
                }
                else if (side is not null && device.Placement == side)
                {
                    device.Placement = null;
                }
            }
        });
        if (_host.GetLink(deviceId) is { } link)
        {
            _ = SendSafeAsync(link, new PlacementMessage(side, Explicit: true));
        }
    }

    /// <summary>Forgets a paired device on both computers.</summary>
    public async Task ForgetAsync(string deviceId)
    {
        if (_host.GetLink(deviceId) is { } link)
        {
            await SendSafeAsync(link, new UnpairMessage()).ConfigureAwait(false);
        }
        _settings.Update(s => s.Devices.RemoveAll(d => d.Id == deviceId));
        await _host.DisconnectAsync(deviceId).ConfigureAwait(false);
    }

    /// <summary>Screens were added, removed or rearranged.</summary>
    public void OnDisplaysChanged()
    {
        var layout = _platform.GetLayout();
        lock (_lock)
        {
            _layout = layout;
        }
    }

    private void OnSettingsChanged()
    {
        _config = _settings.Snapshot();
        ApplyConfig();
        UpdateMouseHook();
        PeersChanged?.Invoke();
    }

    private void ApplyConfig()
    {
        lock (_lock)
        {
            _edgePolicy.Options = _config.Edges;
            _leavePolicy.Options = _config.Edges;
            _hotkeys.Modifiers = _config.SwitchHotkey;
            _host.AllowIncomingPairing = _config.AllowPairingRequests;
            if (_discovery is not null)
            {
                _discovery.Visible = _config.VisibleOnNetwork;
            }
        }
        RefreshActiveSides();
    }

    private void RefreshActiveSides()
    {
        lock (_lock)
        {
            _placements.Clear();
            _activeSides.Clear();
            foreach (var device in _config.Devices)
            {
                if (device.Placement is { } side && _host.GetLink(device.Id) is not null)
                {
                    _placements[side] = device.Id;
                    _activeSides.Add(side);
                }
            }
        }
    }

    private void RaiseControlLeaving(PeerLink target)
    {
        // Called under the lock (possibly on the hook thread): handlers run elsewhere.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                ControlLeaving?.Invoke(target);
            }
            catch (Exception e)
            {
                Log.Error("ControlLeaving handler failed", e);
            }
        });
    }

    private void RaiseStatus()
    {
        try
        {
            StatusChanged?.Invoke(Status);
        }
        catch (Exception e)
        {
            Log.Error("Status handler failed", e);
        }
    }

    private static async Task SendSafeAsync(PeerLink link, ControlMessage message)
    {
        try
        {
            await link.SendControlAsync(message).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Warn($"Sending {message.GetType().Name} to {link.PeerName} failed: {e.Message}");
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _settings.Changed -= OnSettingsChanged;
        _host.PeerConnected -= OnPeerConnected;
        _host.PeerDisconnected -= OnPeerDisconnected;
        lock (_lock)
        {
            EndControlLocked(takeBack: true, leaveFraction: null);
        }
    }
}

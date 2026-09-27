using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace LanConnect.Core.Net;

/// <summary>
/// An authenticated connection to one paired computer: the TLS control channel plus the
/// encrypted input stream (UDP, with the TLS channel as fallback while UDP is not confirmed).
/// </summary>
public sealed class PeerLink : IAsyncDisposable
{
    public const int ProtocolVersion = 1;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(8);
    private const int MinMotionIntervalMs = 2;
    private const int KeepAwakeIntervalMs = 30;
    private const int IdleKeepAliveIntervalMs = 1000;
    private const int UdpConfirmedWindowMs = 3000;

    private readonly FramedConnection _connection;
    private readonly UdpEndpoint _udp;
    private readonly object _lock = new();
    private readonly object _deliveryLock = new();
    private readonly InputStreamSender _sender = new();
    private readonly InputStreamReceiver _receiver = new();
    private readonly InputPacket _packet = new();
    private readonly InputPacket _rxPacket = new();
    private readonly List<ReceivedInput> _received = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly DatagramCrypto _rxCrypto;
    private readonly byte[] _rxKey = DatagramCrypto.NewKey();
    private readonly byte[] _plainBuffer = new byte[2048];
    private readonly byte[] _sealBuffer = new byte[2048];
    private readonly byte[] _openBuffer = new byte[2048];
    private DatagramCrypto? _txCrypto;
    private IPEndPoint _peerUdp;
    private SocketAddress _peerUdpAddress;
    private Thread? _senderThread;
    private long _lastSendMs;
    private long _lastPingMs;
    private long _lastUdpReceiveMs = long.MinValue / 2;
    private long _lastReceiveMs;
    private long _lastControlReceiveMs;
    private long? _pongToSend;
    private bool _ackPending;
    private bool _itemsPending;
    private bool _cryptoDisposed;
    private int _closed;
    private int _wakeRequested;

    public PeerLink(FramedConnection connection, HelloMessage peerHello, UdpEndpoint udp)
    {
        _connection = connection;
        _udp = udp;
        PeerId = connection.PeerFingerprint;
        PeerName = DeviceNames.Sanitize(peerHello.DeviceName);
        PeerAppVersion = peerHello.AppVersion is { Length: <= 32 } version ? version : "?";
        _peerUdp = new IPEndPoint(connection.RemoteEndPoint.Address, peerHello.UdpPort);
        _peerUdpAddress = _peerUdp.Serialize();
        _rxCrypto = new DatagramCrypto(DatagramCrypto.NewSessionId(), _rxKey);
        _lastReceiveMs = _lastControlReceiveMs = NowMs;
    }

    public string PeerId { get; }

    public string PeerName { get; }

    public string PeerAppVersion { get; }

    public bool IsInitiator => _connection.IsInitiator;

    public IPAddress PeerAddress => _connection.RemoteEndPoint.Address;

    /// <summary>Round-trip time of the input path, in milliseconds (-1 until measured).</summary>
    public double RttMs { get; private set; } = -1;

    public bool UdpConfirmed => NowMs - Volatile.Read(ref _lastUdpReceiveMs) < UdpConfirmedWindowMs;

    /// <summary>Milliseconds since anything arrived from the peer.</summary>
    public long SilenceMs => NowMs - Volatile.Read(ref _lastReceiveMs);

    /// <summary>
    /// While true (someone is controlling or being controlled) packets flow at least every 30 ms.
    /// That keeps Wi-Fi radios out of power-save naps that would otherwise add latency spikes.
    /// </summary>
    public bool KeepAwake { get; set; }

    /// <summary>
    /// Raised on the network thread with input to apply, in order. The list is reused: it is
    /// only valid during the callback.
    /// </summary>
    public event Action<PeerLink, IReadOnlyList<ReceivedInput>>? InputReceived;

    public event Action<PeerLink, ControlMessage>? ControlReceived;

    public event Action<PeerLink>? Closed;

    private static readonly long TicksPerMs = Math.Max(1, Stopwatch.Frequency / 1000);

    /// <summary>Monotonic milliseconds (overflow-free for the lifetime of the machine).</summary>
    public static long NowMs => Stopwatch.GetTimestamp() / TicksPerMs;

    /// <summary>Runs until the connection ends. Hello messages must already have been exchanged.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var token = linked.Token;
        _udp.Register(_rxCrypto.SessionId, this);
        _senderThread = new Thread(SenderLoop) { IsBackground = true, Name = $"LanConnect send {PeerName}", Priority = ThreadPriority.AboveNormal };
        _senderThread.Start();
        try
        {
            await _connection.SendAsync(new SessionKeyMessage(_rxCrypto.SessionId, _rxKey), token).ConfigureAwait(false);
            var heartbeat = HeartbeatLoopAsync(token);
            await ReadLoopAsync(token).ConfigureAwait(false);
            await linked.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Log.Warn($"Connection to {PeerName} ended: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            await CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Queues a key, click, scroll or control item; it is resent until acknowledged.</summary>
    public void Send(InputItem item)
    {
        lock (_lock)
        {
            _sender.Enqueue(item);
            _itemsPending = true;
        }
        Wake();
    }

    public void AddMotion(int dx, int dy)
    {
        lock (_lock)
        {
            _sender.AddMotion(dx, dy);
        }
        Wake();
    }

    /// <summary>Signals the sender thread; skips the kernel call when a wake-up is already pending.</summary>
    private void Wake()
    {
        if (Interlocked.Exchange(ref _wakeRequested, 1) == 0)
        {
            _wake.Set();
        }
    }

    public Task SendControlAsync(ControlMessage message) => _connection.SendAsync(message, _cts.Token);

    internal void OnDatagram(ReadOnlySpan<byte> datagram, SocketAddress? from, bool viaUdp)
    {
        // UDP and the TLS fallback deliver on different threads; keep deliveries in order.
        lock (_deliveryLock)
        {
            OnDatagramCore(datagram, from, viaUdp);
        }
    }

    private void OnDatagramCore(ReadOnlySpan<byte> datagram, SocketAddress? from, bool viaUdp)
    {
        var deliver = false;
        lock (_lock)
        {
            var packet = _rxPacket;
            if (_cryptoDisposed ||
                !_rxCrypto.TryOpen(datagram, _openBuffer, out var length) ||
                !InputPacket.TryDecodeInto(_openBuffer.AsSpan(0, length), packet))
            {
                return;
            }

            var now = NowMs;
            _lastReceiveMs = now;
            if (viaUdp)
            {
                _lastUdpReceiveMs = now;
                if (from is not null && !from.Equals(_peerUdpAddress))
                {
                    // Follow the peer if its port/address changed (the packet is authenticated).
                    var copy = new SocketAddress(from.Family, from.Size);
                    from.Buffer.Span[..from.Size].CopyTo(copy.Buffer.Span);
                    _peerUdpAddress = copy;
                    _peerUdp = (IPEndPoint)_peerUdp.Create(copy);
                }
            }

            _sender.Acknowledge(packet.Ack);
            if (packet.Pong is { } pongTimestamp)
            {
                var rtt = now - pongTimestamp;
                RttMs = RttMs < 0 ? rtt : RttMs * 0.8 + rtt * 0.2;
            }
            if (packet.Ping is { } pingTimestamp)
            {
                _pongToSend = pingTimestamp;
            }
            if (packet.Items.Count > 0)
            {
                _ackPending = true;
            }
            if (packet.Items.Count > 0 || packet.Motion is not null)
            {
                // Safe to hand out after unlocking: only this method fills the list, and it runs
                // under the delivery lock.
                _received.Clear();
                _receiver.Receive(packet, _received);
                deliver = _received.Count > 0;
            }
        }

        if (_ackPending || _pongToSend is not null)
        {
            Wake();
        }
        if (deliver)
        {
            try
            {
                InputReceived?.Invoke(this, _received);
            }
            catch (Exception e)
            {
                Log.Error("Applying received input failed", e);
            }
        }
    }

    private void SenderLoop()
    {
        try
        {
            SenderLoopCore();
        }
        catch (Exception e)
        {
            Log.Error($"Sender for {PeerName} stopped", e);
            _ = CloseAsync();
        }
    }

    private void SenderLoopCore()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            int timeout;
            lock (_lock)
            {
                timeout = _sender.MotionDirty ? 1
                    : _sender.UnackedCount > 0 ? RetransmitTimeoutMs()
                    : KeepAwake ? KeepAwakeIntervalMs
                    : IdleKeepAliveIntervalMs;
            }
            _wake.WaitOne(timeout);
            // Reset before looking at the state, so a producer that adds data from now on wakes us again.
            Volatile.Write(ref _wakeRequested, 0);
            if (token.IsCancellationRequested)
            {
                return;
            }

            int sealedLength;
            SocketAddress destination;
            lock (_lock)
            {
                var tx = _txCrypto;
                if (tx is null || _cryptoDisposed)
                {
                    continue; // peer's key not received yet; items wait in the queue
                }
                var now = NowMs;
                var sinceSend = now - _lastSendMs;
                var keepAliveDue = sinceSend >= (KeepAwake ? KeepAwakeIntervalMs : IdleKeepAliveIntervalMs);
                var retransmitDue = _sender.UnackedCount > 0 && sinceSend >= RetransmitTimeoutMs();
                var urgent = _itemsPending || _ackPending || _pongToSend is not null || retransmitDue || keepAliveDue;
                if (!urgent && !(_sender.MotionDirty && sinceSend >= MinMotionIntervalMs))
                {
                    continue;
                }

                _sender.Fill(_packet);
                _packet.Ack = _receiver.Ack;
                _packet.Pong = _pongToSend;
                _packet.Ping = now - _lastPingMs >= 500 ? now : null;
                if (_packet.Ping is not null)
                {
                    _lastPingMs = now;
                }
                _pongToSend = null;
                _ackPending = false;
                _itemsPending = false;
                _lastSendMs = now;
                var plainLength = _packet.Encode(_plainBuffer);
                sealedLength = tx.Seal(_plainBuffer.AsSpan(0, plainLength), _sealBuffer);
                destination = _peerUdpAddress;
            }

            _udp.Send(_sealBuffer.AsSpan(0, sealedLength), destination);
            if (!UdpConfirmed)
            {
                // UDP might be blocked by a firewall: also carry it over the TLS connection.
                try
                {
                    _connection.SendFrameAsync(FrameType.InputTunnel, _sealBuffer.AsMemory(0, sealedLength).ToArray(), token)
                        .GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // the read loop notices a dead connection
                }
            }
        }
    }

    private int RetransmitTimeoutMs() => RttMs < 0 ? 20 : (int)Math.Clamp(RttMs * 1.5 + 5, 8, 150);

    private async Task ReadLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var message = await _connection.ReceiveControlAsync(token, payload =>
            {
                Volatile.Write(ref _lastControlReceiveMs, NowMs);
                OnDatagram(payload, null, viaUdp: false);
            }).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }
            Volatile.Write(ref _lastControlReceiveMs, NowMs);
            Volatile.Write(ref _lastReceiveMs, NowMs);

            switch (message)
            {
                case SessionKeyMessage key:
                    lock (_lock)
                    {
                        _txCrypto?.Dispose();
                        _txCrypto = new DatagramCrypto(key.SessionId, key.Key);
                    }
                    Wake();
                    break;
                case PingMessage ping:
                    await _connection.SendAsync(new PongMessage(ping.Timestamp), token).ConfigureAwait(false);
                    break;
                case PongMessage:
                    break;
                default:
                    try
                    {
                        ControlReceived?.Invoke(this, message);
                    }
                    catch (Exception e)
                    {
                        Log.Error("Handling control message failed", e);
                    }
                    break;
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, token).ConfigureAwait(false);
                if (NowMs - Volatile.Read(ref _lastControlReceiveMs) > HeartbeatTimeout.TotalMilliseconds)
                {
                    Log.Warn($"{PeerName} stopped responding");
                    await _cts.CancelAsync().ConfigureAwait(false);
                    await _connection.DisposeAsync().ConfigureAwait(false);
                    return;
                }
                await _connection.SendAsync(new PingMessage(NowMs), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }
        _udp.Unregister(_rxCrypto.SessionId);
        await _cts.CancelAsync().ConfigureAwait(false);
        _wake.Set();
        if (_senderThread is { } thread && thread != Thread.CurrentThread)
        {
            thread.Join(2000);
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
        lock (_lock)
        {
            _sender.Clear();
        }
        try
        {
            Closed?.Invoke(this);
        }
        catch (Exception e)
        {
            Log.Error("Handling disconnect failed", e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        lock (_lock)
        {
            if (_cryptoDisposed)
            {
                return;
            }
            _cryptoDisposed = true;
            _rxCrypto.Dispose();
            _txCrypto?.Dispose();
            _txCrypto = null;
        }
    }
}

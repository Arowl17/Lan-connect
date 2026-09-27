using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace LanConnect.Core.Net;

/// <summary>
/// Owns the TCP listener and UDP socket, accepts and dials connections, runs pairing, and keeps
/// exactly one live <see cref="PeerLink"/> per paired computer.
/// </summary>
public sealed class NetworkHost : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>On a LAN a live computer answers in milliseconds; don't wait long on stale addresses.</summary>
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(4);

    private readonly DeviceIdentity _identity;
    private readonly Func<string> _deviceName;
    private readonly ITrustStore _trust;
    private readonly string _appVersion;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, PeerLink> _links = new();
    private readonly ConcurrentDictionary<string, byte> _dialing = new();
    private int _pairingInProgress;

    public NetworkHost(DeviceIdentity identity, Func<string> deviceName, ITrustStore trust, string appVersion, int tcpPort = FramedConnection.DefaultPort, int udpPort = FramedConnection.DefaultPort)
    {
        _identity = identity;
        _deviceName = deviceName;
        _trust = trust;
        _appVersion = appVersion;
        _listener = new TcpListener(IPAddress.Any, tcpPort);
        try
        {
            _listener.Start();
        }
        catch (SocketException) when (tcpPort != 0)
        {
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
        }
        TcpPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Udp = new UdpEndpoint(udpPort);
    }

    public int TcpPort { get; }

    public UdpEndpoint Udp { get; }

    public string OwnId => _identity.Fingerprint;

    /// <summary>Accept incoming pairing requests (the user still has to confirm each one).</summary>
    public bool AllowIncomingPairing { get; set; } = true;

    public event Action<PeerLink>? PeerConnected;

    public event Action<PeerLink>? PeerDisconnected;

    /// <summary>Another computer wants to pair; show the code and call Confirm or Reject.</summary>
    public event Action<PairingSession>? PairingRequested;

    public IReadOnlyCollection<PeerLink> Links => _links.Values.ToList();

    public PeerLink? GetLink(string peerId) => _links.TryGetValue(peerId, out var link) ? link : null;

    public void Start() => _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

    /// <summary>Starts pairing with a computer found on the network (this side shows the code too).</summary>
    public async Task<PairingSession> PairAsync(IPEndPoint endPoint, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _pairingInProgress, 1) != 0)
        {
            throw new InvalidOperationException("Another pairing is already in progress.");
        }
        FramedConnection? connection = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            connection = await FramedConnection.ConnectAsync(endPoint, _identity, HandshakeTimeout, cts.Token).ConfigureAwait(false);
            cts.CancelAfter(HandshakeTimeout);
            await connection.SendAsync(Hello(ConnectPurpose.Pair), cts.Token).ConfigureAwait(false);
            var hello = await connection.ReceiveControlAsync(cts.Token).ConfigureAwait(false) as HelloMessage
                ?? throw new InvalidDataException("The other computer declined the pairing request.");
            var session = await PairingSession.RunInitiatorAsync(connection, OwnId, hello.DeviceName, cts.Token).ConfigureAwait(false);
            _ = FinishPairingAsync(session);
            return session;
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            Volatile.Write(ref _pairingInProgress, 0);
            throw;
        }
    }

    private async Task FinishPairingAsync(PairingSession session)
    {
        try
        {
            await session.CompleteAsync(_trust, _cts.Token).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _pairingInProgress, 0);
        }
    }

    /// <summary>Connects to a paired computer unless already connected. Safe to call repeatedly.</summary>
    public void EnsureConnected(string peerId, IEnumerable<IPEndPoint> candidates)
    {
        if (_links.ContainsKey(peerId) || !_trust.IsTrusted(peerId) || !_dialing.TryAdd(peerId, 0))
        {
            return;
        }
        var list = candidates.Distinct().ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var endPoint in list)
                {
                    if (_links.ContainsKey(peerId) || _cts.IsCancellationRequested)
                    {
                        return;
                    }
                    if (await TryDialAsync(peerId, endPoint).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
            finally
            {
                _dialing.TryRemove(peerId, out _);
            }
        });
    }

    public async Task DisconnectAsync(string peerId)
    {
        if (_links.TryGetValue(peerId, out var link))
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> TryDialAsync(string peerId, IPEndPoint endPoint)
    {
        FramedConnection? connection = null;
        try
        {
            connection = await FramedConnection.ConnectAsync(endPoint, _identity, DialTimeout, _cts.Token).ConfigureAwait(false);
            if (connection.PeerFingerprint != peerId)
            {
                // Something else answered at that address (e.g. DHCP handed it to another machine).
                Log.Warn($"Unexpected device at {endPoint}; expected {DeviceIdentity.ShortFingerprint(peerId)}");
                await connection.DisposeAsync().ConfigureAwait(false);
                return false;
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(HandshakeTimeout);
            await connection.SendAsync(Hello(ConnectPurpose.Session), cts.Token).ConfigureAwait(false);
            if (await connection.ReceiveControlAsync(cts.Token).ConfigureAwait(false) is not HelloMessage { Purpose: ConnectPurpose.Session } hello)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return false;
            }
            StartLink(connection, hello);
            return true;
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or InvalidDataException or System.Security.Authentication.AuthenticationException or System.Text.Json.JsonException)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = Task.Run(() => HandleIncomingAsync(client, token), token);
        }
    }

    private async Task HandleIncomingAsync(TcpClient client, CancellationToken token)
    {
        FramedConnection? connection = null;
        var handedOff = false;
        try
        {
            connection = await FramedConnection.AcceptAsync(client, _identity, HandshakeTimeout, token).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(HandshakeTimeout);
            if (await connection.ReceiveControlAsync(cts.Token).ConfigureAwait(false) is not HelloMessage hello)
            {
                return;
            }

            switch (hello.Purpose)
            {
                case ConnectPurpose.Session when _trust.IsTrusted(connection.PeerFingerprint):
                    await connection.SendAsync(Hello(ConnectPurpose.Session), cts.Token).ConfigureAwait(false);
                    StartLink(connection, hello);
                    handedOff = true;
                    break;

                case ConnectPurpose.Pair when AllowIncomingPairing && PairingRequested is not null:
                    if (Interlocked.Exchange(ref _pairingInProgress, 1) != 0)
                    {
                        return; // one at a time
                    }
                    try
                    {
                        await connection.SendAsync(Hello(ConnectPurpose.Pair), cts.Token).ConfigureAwait(false);
                        var session = await PairingSession.RunResponderAsync(connection, OwnId, hello.DeviceName, cts.Token).ConfigureAwait(false);
                        handedOff = true;
                        _ = FinishPairingAsync(session);
                        PairingRequested?.Invoke(session);
                    }
                    finally
                    {
                        if (!handedOff)
                        {
                            Volatile.Write(ref _pairingInProgress, 0);
                        }
                    }
                    break;

                default:
                    Log.Info($"Rejected connection from {connection.RemoteEndPoint.Address} (not paired)");
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Info($"Incoming connection failed: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            if (!handedOff)
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    client.Dispose();
                }
            }
        }
    }

    private void StartLink(FramedConnection connection, HelloMessage hello)
    {
        var link = new PeerLink(connection, hello, Udp);

        // Both computers may dial each other at the same moment. Both sides keep the connection
        // started by the computer with the smaller id, so they always agree on which one survives.
        var preferredInitiator = string.CompareOrdinal(OwnId, link.PeerId) < 0 ? OwnId : link.PeerId;
        string InitiatorOf(PeerLink l) => l.IsInitiator ? OwnId : l.PeerId;

        PeerLink? replaced = null;
        var accepted = true;
        _links.AddOrUpdate(link.PeerId, link, (_, existing) =>
        {
            if (InitiatorOf(existing) == preferredInitiator && InitiatorOf(link) != preferredInitiator)
            {
                accepted = false;
                return existing;
            }
            replaced = existing;
            accepted = true;
            return link;
        });

        if (!accepted)
        {
            _ = link.DisposeAsync().AsTask();
            return;
        }

        link.Closed += OnLinkClosed;
        if (replaced is not null)
        {
            replaced.Closed -= OnLinkClosed;
            _ = replaced.DisposeAsync().AsTask();
            PeerDisconnected?.Invoke(replaced);
        }
        Log.Info($"Connected to {link.PeerName} ({link.PeerAddress})");
        // Let listeners subscribe to the link's events before any input can arrive.
        try
        {
            PeerConnected?.Invoke(link);
        }
        catch (Exception e)
        {
            Log.Error("PeerConnected handler failed", e);
        }
        _ = Task.Run(() => link.RunAsync(_cts.Token));
    }

    private void OnLinkClosed(PeerLink link)
    {
        if (_links.TryRemove(new KeyValuePair<string, PeerLink>(link.PeerId, link)))
        {
            Log.Info($"Disconnected from {link.PeerName}");
            PeerDisconnected?.Invoke(link);
        }
    }

    private HelloMessage Hello(ConnectPurpose purpose) =>
        new(PeerLink.ProtocolVersion, _deviceName(), purpose, Udp.Port, _appVersion);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        foreach (var link in _links.Values)
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
        Udp.Dispose();
        _cts.Dispose();
    }
}

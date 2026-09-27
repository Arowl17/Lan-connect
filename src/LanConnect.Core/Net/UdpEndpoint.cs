using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace LanConnect.Core.Net;

/// <summary>The app's single UDP socket for input datagrams; routes them to links by session id.</summary>
public sealed class UdpEndpoint : IDisposable
{
    private readonly Socket _socket;
    private readonly ConcurrentDictionary<uint, PeerLink> _links = new();
    private readonly Thread _receiveThread;
    private volatile bool _disposed;

    public UdpEndpoint(int preferredPort)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            // Otherwise an ICMP "port unreachable" makes the next ReceiveFrom throw on Windows.
            const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
            _socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        try
        {
            _socket.Bind(new IPEndPoint(IPAddress.Any, preferredPort));
        }
        catch (SocketException)
        {
            _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "LanConnect UDP receive", Priority = ThreadPriority.AboveNormal };
        _receiveThread.Start();
    }

    public int Port { get; }

    /// <summary>Tests: drop every outgoing datagram (a firewall blocking UDP).</summary>
    internal volatile bool BlockSends;

    /// <summary>Tests: drop this fraction of outgoing datagrams (bad Wi-Fi).</summary>
    internal double LossRate;

    public void Register(uint sessionId, PeerLink link) => _links[sessionId] = link;

    public void Unregister(uint sessionId) => _links.TryRemove(sessionId, out _);

    public void Send(ReadOnlySpan<byte> datagram, SocketAddress to)
    {
        if (BlockSends || (LossRate > 0 && Random.Shared.NextDouble() < LossRate))
        {
            return;
        }
        try
        {
            _socket.SendTo(datagram, SocketFlags.None, to);
        }
        catch (SocketException)
        {
            // Network hiccup: reliable items are resent, motion is cumulative.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ReceiveLoop()
    {
        var buffer = new byte[2048];
        // Reused for every packet: no per-packet allocations on the receive path.
        var from = new SocketAddress(AddressFamily.InterNetwork);
        while (!_disposed)
        {
            int length;
            try
            {
                length = _socket.ReceiveFrom(buffer, SocketFlags.None, from);
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (length < DatagramCrypto.Overhead)
            {
                continue;
            }
            var sessionId = BinaryPrimitives.ReadUInt32BigEndian(buffer);
            if (_links.TryGetValue(sessionId, out var link))
            {
                try
                {
                    link.OnDatagram(buffer.AsSpan(0, length), from, viaUdp: true);
                }
                catch (Exception e)
                {
                    Log.Error("Processing a datagram failed", e);
                }
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _socket.Dispose();
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace LanConnect.Core.Net;

public sealed record DiscoveredDevice(string Id, string Name, IPAddress Address, int TcpPort, DateTime LastSeenUtc);

/// <summary>
/// Finds other Lan Connect computers on the local network with small UDP broadcasts,
/// so nobody has to type IP addresses.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    public const int DefaultPort = 47811;
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExpireAfter = TimeSpan.FromSeconds(12);

    private readonly string _deviceId;
    private readonly Func<string> _deviceName;
    private readonly int _tcpPort;
    private readonly int _discoveryPort;
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _devices = new();
    private readonly CancellationTokenSource _cts = new();
    private Socket? _socket;

    public DiscoveryService(string deviceId, Func<string> deviceName, int tcpPort, int discoveryPort = DefaultPort)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;
        _tcpPort = tcpPort;
        _discoveryPort = discoveryPort;
    }

    /// <summary>When false this computer doesn't announce itself (it still listens).</summary>
    public bool Visible { get; set; } = true;

    public event Action<DiscoveredDevice>? DeviceSeen;

    public IReadOnlyList<DiscoveredDevice> Devices
    {
        get
        {
            var cutoff = DateTime.UtcNow - ExpireAfter;
            return _devices.Values.Where(d => d.LastSeenUtc >= cutoff).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public void Start()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        if (OperatingSystem.IsWindows())
        {
            const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
            socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        socket.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
        _socket = socket;
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _ = Task.Run(() => AnnounceLoopAsync(_cts.Token));
    }

    /// <summary>Broadcasts right away and asks everyone to answer (used when the pairing window opens).</summary>
    public void AnnounceNow() => Broadcast(query: true);

    private async Task AnnounceLoopAsync(CancellationToken token)
    {
        Broadcast(query: true);
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(AnnounceInterval, token).ConfigureAwait(false);
                Broadcast(query: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Broadcast(bool query)
    {
        if (_socket is null || !Visible)
        {
            return;
        }
        var payload = Encode(query);
        foreach (var target in BroadcastAddresses())
        {
            try
            {
                _socket.SendTo(payload, new IPEndPoint(target, _discoveryPort));
            }
            catch (SocketException)
            {
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[1024];
        while (!token.IsCancellationRequested && _socket is { } socket)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token).ConfigureAwait(false);
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

            if (!TryDecode(buffer.AsSpan(0, result.ReceivedBytes), out var beacon) || beacon.Id == _deviceId)
            {
                continue;
            }
            var from = (IPEndPoint)result.RemoteEndPoint;
            var device = new DiscoveredDevice(beacon.Id, beacon.Name, from.Address, beacon.Port, DateTime.UtcNow);
            _devices[beacon.Id] = device;
            DeviceSeen?.Invoke(device);

            if (beacon.Query && Visible)
            {
                try
                {
                    await socket.SendToAsync(Encode(query: false), SocketFlags.None, new IPEndPoint(from.Address, _discoveryPort), token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }
    }

    private byte[] Encode(bool query) => JsonSerializer.SerializeToUtf8Bytes(new Beacon("lan-connect", 1, _deviceId, _deviceName(), _tcpPort, query));

    private static bool TryDecode(ReadOnlySpan<byte> data, out Beacon beacon)
    {
        beacon = default!;
        try
        {
            var decoded = JsonSerializer.Deserialize<Beacon>(data);
            if (decoded is null || decoded.App != "lan-connect" || string.IsNullOrEmpty(decoded.Id) || decoded.Port is <= 0 or > 65535)
            {
                return false;
            }
            beacon = decoded with { Name = decoded.Name.Length > 64 ? decoded.Name[..64] : decoded.Name };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                    {
                        continue;
                    }
                    var ip = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++)
                    {
                        broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    }
                    addresses.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        return addresses;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket?.Dispose();
        _cts.Dispose();
    }

    private sealed record Beacon(string App, int V, string Id, string Name, int Port, bool Query);
}

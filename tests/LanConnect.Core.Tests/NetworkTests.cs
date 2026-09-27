using System.Collections.Concurrent;
using System.Net;
using LanConnect.Core.Input;
using LanConnect.Core.Layout;
using LanConnect.Core.Net;

namespace LanConnect.Core.Tests;

internal sealed class MemoryTrustStore : ITrustStore
{
    public ConcurrentDictionary<string, string> Trusted { get; } = new();

    public bool IsTrusted(string fingerprint) => Trusted.ContainsKey(fingerprint);

    public void Trust(string fingerprint, string name) => Trusted[fingerprint] = name;
}

internal sealed class TestDevice : IAsyncDisposable
{
    public TestDevice(string name)
    {
        Name = name;
        Identity = DeviceIdentity.Create();
        Host = new NetworkHost(Identity, () => Name, Trust, "test", tcpPort: 0, udpPort: 0);
        Host.Start();
    }

    public string Name { get; }

    public DeviceIdentity Identity { get; }

    public MemoryTrustStore Trust { get; } = new();

    public NetworkHost Host { get; }

    public IPEndPoint EndPoint => new(IPAddress.Loopback, Host.TcpPort);

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        Identity.Dispose();
    }
}

public class NetworkTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task IdentityRoundTripsThroughPkcs12()
    {
        using var identity = DeviceIdentity.Create();
        using var loaded = DeviceIdentity.FromPkcs12(identity.ExportPkcs12());
        Assert.Equal(identity.Fingerprint, loaded.Fingerprint);
        Assert.True(loaded.Certificate.HasPrivateKey);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task PairConnectAndSendInputEndToEnd()
    {
        await using var pc = new TestDevice("Gaming PC");
        await using var laptop = new TestDevice("Work Laptop");

        var incoming = new TaskCompletionSource<PairingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        laptop.Host.PairingRequested += s => incoming.TrySetResult(s);

        var outgoing = await pc.Host.PairAsync(laptop.EndPoint).WaitAsync(Timeout);
        var request = await incoming.Task.WaitAsync(Timeout);

        Assert.Equal(outgoing.Code, request.Code);
        Assert.Equal("Work Laptop", outgoing.PeerName);
        Assert.Equal("Gaming PC", request.PeerName);
        Assert.Equal(laptop.Identity.Fingerprint, outgoing.PeerId);
        Assert.Equal(pc.Identity.Fingerprint, request.PeerId);

        outgoing.Confirm();
        request.Confirm();
        Assert.True(await outgoing.Result.WaitAsync(Timeout));
        Assert.True(await request.Result.WaitAsync(Timeout));
        Assert.True(pc.Trust.IsTrusted(laptop.Identity.Fingerprint));
        Assert.True(laptop.Trust.IsTrusted(pc.Identity.Fingerprint));

        var pcConnected = new TaskCompletionSource<PeerLink>(TaskCreationOptions.RunContinuationsAsynchronously);
        var laptopConnected = new TaskCompletionSource<PeerLink>(TaskCreationOptions.RunContinuationsAsynchronously);
        pc.Host.PeerConnected += l => pcConnected.TrySetResult(l);
        laptop.Host.PeerConnected += l => laptopConnected.TrySetResult(l);

        pc.Host.EnsureConnected(laptop.Identity.Fingerprint, [laptop.EndPoint]);
        var pcLink = await pcConnected.Task.WaitAsync(Timeout);
        var laptopLink = await laptopConnected.Task.WaitAsync(Timeout);

        var received = new BlockingCollection<ReceivedInput>();
        laptopLink.InputReceived += (_, inputs) =>
        {
            foreach (var i in inputs)
            {
                received.Add(i);
            }
        };

        pcLink.KeepAwake = true;
        pcLink.Send(InputItem.Enter(Side.Left, 0.5));
        pcLink.AddMotion(10, 5);
        pcLink.Send(InputItem.FromEvent(InputEvent.MouseButtonEvent(MouseButton.Left, true)));
        pcLink.AddMotion(-3, 7);
        pcLink.Send(InputItem.FromEvent(InputEvent.MouseButtonEvent(MouseButton.Left, false)));
        pcLink.Send(InputItem.FromEvent(InputEvent.KeyEvent(0x1E, 0x41, false, true)));

        // Expect: Enter, motion to (10,5), click down, motion (-3,7), click up, key.
        var items = new List<InputItem>();
        int x = 0, y = 0;
        var positions = new List<(int, int)>();
        using var cts = new CancellationTokenSource(Timeout);
        while (items.Count < 4 || (x, y) != (7, 12))
        {
            var r = received.Take(cts.Token);
            if (r.IsMotion)
            {
                x += r.Dx;
                y += r.Dy;
            }
            else
            {
                items.Add(r.Item);
                positions.Add((x, y));
            }
        }

        Assert.Equal(InputItemType.Enter, items[0].Type);
        Assert.Equal(Side.Left, items[0].Side);
        Assert.Equal(InputItemType.Button, items[1].Type);
        Assert.Equal((10, 5), positions[1]);
        Assert.Equal(InputItemType.Button, items[2].Type);
        Assert.Equal((7, 12), positions[2]);
        Assert.Equal(InputItemType.Key, items[3].Type);
        Assert.Equal((ushort)0x1E, items[3].Event.ScanCode);
    }

    [Fact]
    public async Task RejectedPairingTrustsNobody()
    {
        await using var a = new TestDevice("A");
        await using var b = new TestDevice("B");
        var incoming = new TaskCompletionSource<PairingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Host.PairingRequested += s => incoming.TrySetResult(s);

        var outgoing = await a.Host.PairAsync(b.EndPoint).WaitAsync(Timeout);
        var request = await incoming.Task.WaitAsync(Timeout);
        request.Reject();

        Assert.False(await outgoing.Result.WaitAsync(Timeout)); // A finds out without having to decide
        Assert.False(await request.Result.WaitAsync(Timeout));
        Assert.Empty(a.Trust.Trusted);
        Assert.Empty(b.Trust.Trusted);
    }

    [Fact]
    public async Task DeclinedPairingRequestsAreNotRepeatedImmediately()
    {
        await using var a = new TestDevice("A");
        await using var b = new TestDevice("B");
        var requests = 0;
        b.Host.PairingRequested += s =>
        {
            Interlocked.Increment(ref requests);
            s.Reject();
        };

        var first = await a.Host.PairAsync(b.EndPoint).WaitAsync(Timeout);
        Assert.False(await first.Result.WaitAsync(Timeout));

        // Let B finish the first attempt, so it's the cooldown (not "busy") that refuses.
        await Task.Delay(500);
        // Asking again right away doesn't pop up another dialog on B.
        await Assert.ThrowsAnyAsync<Exception>(() => a.Host.PairAsync(b.EndPoint).WaitAsync(Timeout));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task UnpairedComputerCannotConnect()
    {
        await using var intruder = new TestDevice("Intruder");
        await using var laptop = new TestDevice("Laptop");
        // The intruder trusts the laptop, but the laptop never paired with the intruder.
        intruder.Trust.Trust(laptop.Identity.Fingerprint, "Laptop");

        var connected = false;
        laptop.Host.PeerConnected += _ => connected = true;
        intruder.Host.PeerConnected += _ => connected = true;
        intruder.Host.EnsureConnected(laptop.Identity.Fingerprint, [laptop.EndPoint]);
        await Task.Delay(1500);
        Assert.False(connected);
        Assert.Empty(laptop.Host.Links);
    }

    [Fact]
    public async Task DialingTheWrongDeviceIsRefused()
    {
        await using var pc = new TestDevice("PC");
        await using var laptop = new TestDevice("Laptop");
        await using var stranger = new TestDevice("Stranger");
        pc.Trust.Trust(laptop.Identity.Fingerprint, "Laptop");
        stranger.Trust.Trust(pc.Identity.Fingerprint, "PC");

        var connected = false;
        pc.Host.PeerConnected += _ => connected = true;
        // The laptop's address now belongs to a stranger (e.g. DHCP reassigned it).
        pc.Host.EnsureConnected(laptop.Identity.Fingerprint, [stranger.EndPoint]);
        await Task.Delay(1500);
        Assert.False(connected);
    }

    [Fact]
    public async Task SimultaneousDialsEndWithOneLinkOnEachSide()
    {
        await using var a = new TestDevice("A");
        await using var b = new TestDevice("B");
        a.Trust.Trust(b.Identity.Fingerprint, "B");
        b.Trust.Trust(a.Identity.Fingerprint, "A");

        a.Host.EnsureConnected(b.Identity.Fingerprint, [b.EndPoint]);
        b.Host.EnsureConnected(a.Identity.Fingerprint, [a.EndPoint]);

        using var cts = new CancellationTokenSource(Timeout);
        while (a.Host.Links.Count != 1 || b.Host.Links.Count != 1)
        {
            await Task.Delay(50, cts.Token);
        }
        await Task.Delay(1000, cts.Token);
        Assert.Single(a.Host.Links);
        Assert.Single(b.Host.Links);
        // Both sides kept the same connection (one initiated it, the other accepted it).
        Assert.NotEqual(a.Host.Links.Single().IsInitiator, b.Host.Links.Single().IsInitiator);
    }
}

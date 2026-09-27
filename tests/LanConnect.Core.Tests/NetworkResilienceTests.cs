using System.Collections.Concurrent;
using System.Net;
using LanConnect.Core.Input;
using LanConnect.Core.Layout;
using LanConnect.Core.Net;

namespace LanConnect.Core.Tests;

public class NetworkResilienceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static async Task<(TestDevice A, TestDevice B, PeerLink LinkA, PeerLink LinkB)> ConnectAsync()
    {
        var a = new TestDevice("A");
        var b = new TestDevice("B");
        a.Trust.Trust(b.Identity.Fingerprint, "B");
        b.Trust.Trust(a.Identity.Fingerprint, "A");
        var linkA = new TaskCompletionSource<PeerLink>(TaskCreationOptions.RunContinuationsAsynchronously);
        var linkB = new TaskCompletionSource<PeerLink>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Host.PeerConnected += l => linkA.TrySetResult(l);
        b.Host.PeerConnected += l => linkB.TrySetResult(l);
        a.Host.EnsureConnected(b.Identity.Fingerprint, [b.EndPoint]);
        return (a, b, await linkA.Task.WaitAsync(Timeout), await linkB.Task.WaitAsync(Timeout));
    }

    /// <summary>Sends keys and movement from A to B and checks everything arrives exactly once, in order.</summary>
    private static async Task SendAndVerifyAsync(PeerLink from, PeerLink to, int keys)
    {
        var received = new BlockingCollection<ReceivedInput>();
        to.InputReceived += (_, inputs) =>
        {
            foreach (var i in inputs)
            {
                received.Add(i);
            }
        };

        from.KeepAwake = true;
        for (var i = 0; i < keys; i++)
        {
            from.AddMotion(3, -2);
            from.Send(InputItem.FromEvent(InputEvent.KeyEvent((ushort)(i % 80 + 1), 0x41, false, i % 2 == 0)));
            await Task.Delay(2);
        }
        from.AddMotion(1, 1);

        var items = new List<InputItem>();
        int x = 0, y = 0;
        using var cts = new CancellationTokenSource(Timeout);
        while (items.Count < keys || (x, y) != (keys * 3 + 1, keys * -2 + 1))
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
            }
        }
        Assert.Equal(keys, items.Count);
        for (var i = 0; i < keys; i++)
        {
            Assert.Equal((ushort)(i % 80 + 1), items[i].Event.ScanCode);
            Assert.Equal(i % 2 == 0, items[i].Event.Down);
        }
    }

    [Fact]
    public async Task InputStillArrivesWhenAFirewallBlocksUdp()
    {
        var (a, b, linkA, linkB) = await ConnectAsync();
        await using var _ = a;
        await using var __ = b;
        a.Host.Udp.BlockSends = true;
        b.Host.Udp.BlockSends = true;

        await SendAndVerifyAsync(linkA, linkB, keys: 60);
        Assert.False(linkA.UdpConfirmed);
    }

    [Fact]
    public async Task InputSurvivesHeavyPacketLoss()
    {
        var (a, b, linkA, linkB) = await ConnectAsync();
        await using var _ = a;
        await using var __ = b;
        // Wait until UDP works, then make it bad: 30% of packets lost in both directions.
        using (var cts = new CancellationTokenSource(Timeout))
        {
            while (!linkA.UdpConfirmed || !linkB.UdpConfirmed)
            {
                await Task.Delay(20, cts.Token);
            }
        }
        a.Host.Udp.LossRate = 0.3;
        b.Host.Udp.LossRate = 0.3;

        await SendAndVerifyAsync(linkA, linkB, keys: 150);
    }

    [Fact]
    public async Task ReconnectsAfterTheOtherComputerRestarts()
    {
        var (a, b, _, _) = await ConnectAsync();
        await using var __ = a;
        var bIdentity = b.Identity;

        // B's app quits: A notices the disconnect.
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Host.PeerDisconnected += _ => disconnected.TrySetResult();
        await b.Host.DisposeAsync();
        await disconnected.Task.WaitAsync(Timeout);
        Assert.Empty(a.Host.Links);

        // B starts again (same identity, new port) and A reconnects to it.
        await using var restarted = new NetworkHost(bIdentity, () => "B", b.Trust, "test", tcpPort: 0, udpPort: 0);
        restarted.Start();
        var reconnected = new TaskCompletionSource<PeerLink>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Host.PeerConnected += l => reconnected.TrySetResult(l);
        a.Host.EnsureConnected(bIdentity.Fingerprint, [new IPEndPoint(IPAddress.Loopback, restarted.TcpPort)]);
        var link = await reconnected.Task.WaitAsync(Timeout);
        Assert.Equal(bIdentity.Fingerprint, link.PeerId);
        bIdentity.Dispose();
    }

    [Fact]
    public async Task RoundTripTimeIsMeasured()
    {
        var (a, b, linkA, _) = await ConnectAsync();
        await using var _ = a;
        await using var __ = b;
        linkA.KeepAwake = true;
        using var cts = new CancellationTokenSource(Timeout);
        while (linkA.RttMs < 0)
        {
            await Task.Delay(20, cts.Token);
        }
        Assert.InRange(linkA.RttMs, 0, 1000);
    }
}

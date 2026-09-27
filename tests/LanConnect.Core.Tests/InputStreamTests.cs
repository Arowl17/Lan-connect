using LanConnect.Core.Input;
using LanConnect.Core.Layout;
using LanConnect.Core.Net;

namespace LanConnect.Core.Tests;

public class InputStreamTests
{
    [Fact]
    public void PacketRoundTripsThroughEncoding()
    {
        var packet = new InputPacket { Ack = 42, Motion = new MotionStamp(7, -100, 250), Ping = 123456789 };
        packet.Items.Add(new SequencedItem(1, new MotionStamp(3, 1, 2), InputItem.FromEvent(InputEvent.KeyEvent(0x1D, 0xA2, true, true))));
        packet.Items.Add(new SequencedItem(2, new MotionStamp(4, 1, 2), InputItem.FromEvent(InputEvent.MouseButtonEvent(MouseButton.X2, false))));
        packet.Items.Add(new SequencedItem(3, new MotionStamp(5, 1, 2), InputItem.FromEvent(InputEvent.Wheel(-240, true))));
        packet.Items.Add(new SequencedItem(4, new MotionStamp(6, 1, 2), InputItem.Enter(Side.Left, 0.25)));
        packet.Items.Add(new SequencedItem(5, new MotionStamp(6, 1, 2), InputItem.TakeBack()));

        var buffer = new byte[1500];
        var length = packet.Encode(buffer);
        Assert.True(InputPacket.TryDecode(buffer.AsSpan(0, length), out var decoded));

        Assert.Equal(42u, decoded.Ack);
        Assert.Equal(packet.Motion, decoded.Motion);
        Assert.Equal(packet.Ping, decoded.Ping);
        Assert.Null(decoded.Pong);
        Assert.Equal(5, decoded.Items.Count);
        Assert.Equal(packet.Items[0], decoded.Items[0]);
        Assert.Equal(packet.Items[1], decoded.Items[1]);
        Assert.Equal(packet.Items[2], decoded.Items[2]);
        Assert.Equal(Side.Left, decoded.Items[3].Item.Side);
        Assert.Equal(0.25, decoded.Items[3].Item.Fraction, 3);
        Assert.Equal(InputItemType.TakeBack, decoded.Items[4].Item.Type);
    }

    [Fact]
    public void TruncatedOrGarbagePacketsAreRejected()
    {
        var packet = new InputPacket { Ack = 1 };
        packet.Items.Add(new SequencedItem(1, default, InputItem.FromEvent(InputEvent.KeyEvent(1, 2, false, true))));
        var buffer = new byte[256];
        var length = packet.Encode(buffer);
        for (var cut = 0; cut < length; cut++)
        {
            Assert.False(InputPacket.TryDecode(buffer.AsSpan(0, cut), out _));
        }
        Assert.False(InputPacket.TryDecode([0, 0, 0, 0, 0, 200], out _));
    }

    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(0.3, 2)]
    [InlineData(0.6, 3)]
    public void LossyReorderingChannelDeliversEverythingExactlyOnceAtTheRightPosition(double lossRate, int seed)
    {
        var random = new Random(seed);
        var sender = new InputStreamSender();
        var receiver = new InputStreamReceiver();
        var inFlight = new List<InputPacket>();
        var output = new List<ReceivedInput>();

        var expectedItems = new List<(InputItem Item, int X, int Y)>();
        int sentX = 0, sentY = 0;

        void Transmit()
        {
            var packet = new InputPacket();
            sender.Fill(packet);
            var copy = Roundtrip(packet);
            if (random.NextDouble() >= lossRate)
            {
                inFlight.Add(copy);
            }
        }

        void DeliverSome()
        {
            // Deliver a random subset in random order (reordering), keep the rest in flight.
            for (var i = inFlight.Count - 1; i >= 0; i--)
            {
                if (random.NextDouble() < 0.6)
                {
                    var p = inFlight[i];
                    inFlight.RemoveAt(i);
                    receiver.Receive(p, output);
                    if (random.NextDouble() >= lossRate)
                    {
                        sender.Acknowledge(receiver.Ack); // ack packet may be lost too
                    }
                }
            }
        }

        for (var step = 0; step < 3000; step++)
        {
            var roll = random.Next(10);
            if (roll < 7)
            {
                var dx = random.Next(-20, 21);
                var dy = random.Next(-20, 21);
                sender.AddMotion(dx, dy);
                sentX += dx;
                sentY += dy;
            }
            else if (sender.UnackedCount < InputPacket.MaxItems)
            {
                var item = roll switch
                {
                    7 => InputItem.FromEvent(InputEvent.KeyEvent((ushort)random.Next(1, 90), 0x41, false, random.Next(2) == 0)),
                    8 => InputItem.FromEvent(InputEvent.MouseButtonEvent(MouseButton.Left, random.Next(2) == 0)),
                    _ => InputItem.FromEvent(InputEvent.Wheel(120, false)),
                };
                sender.Enqueue(item);
                expectedItems.Add((item, sentX, sentY));
            }
            Transmit();
            DeliverSome();
        }

        // Keep retransmitting until everything is acknowledged (like the retransmit timer does).
        for (var i = 0; i < 1000 && (sender.UnackedCount > 0 || inFlight.Count > 0); i++)
        {
            Transmit();
            DeliverSome();
        }
        // A final clean packet so the last motion total arrives.
        receiver.Receive(Roundtrip(FillNew(sender)), output);

        Assert.Equal(0, sender.UnackedCount);

        int appliedX = 0, appliedY = 0;
        var delivered = new List<(InputItem Item, int X, int Y)>();
        foreach (var r in output)
        {
            if (r.IsMotion)
            {
                appliedX += r.Dx;
                appliedY += r.Dy;
            }
            else
            {
                delivered.Add((r.Item, appliedX, appliedY));
            }
        }

        // Every key/click/scroll exactly once, in order, at the exact cursor position it was made at.
        Assert.Equal(expectedItems, delivered);
        // No movement lost or duplicated.
        Assert.Equal(sentX, appliedX);
        Assert.Equal(sentY, appliedY);
    }

    private static InputPacket FillNew(InputStreamSender sender)
    {
        var p = new InputPacket();
        sender.Fill(p);
        return p;
    }

    private static InputPacket Roundtrip(InputPacket packet)
    {
        var buffer = new byte[2048];
        var length = packet.Encode(buffer);
        Assert.True(InputPacket.TryDecode(buffer.AsSpan(0, length), out var decoded));
        return decoded;
    }
}

public class CryptoTests
{
    [Fact]
    public void SealAndOpenRoundTrip()
    {
        var key = DatagramCrypto.NewKey();
        using var tx = new DatagramCrypto(77, key);
        using var rx = new DatagramCrypto(77, key);
        var message = "hello keys"u8.ToArray();
        var datagram = new byte[message.Length + DatagramCrypto.Overhead];
        var length = tx.Seal(message, datagram);
        var plain = new byte[64];
        Assert.True(rx.TryOpen(datagram.AsSpan(0, length), plain, out var plainLength));
        Assert.Equal(message, plain[..plainLength]);
    }

    [Fact]
    public void TamperedReplayedOrForeignDatagramsAreRejected()
    {
        var key = DatagramCrypto.NewKey();
        using var tx = new DatagramCrypto(5, key);
        using var rx = new DatagramCrypto(5, key);
        using var other = new DatagramCrypto(5, DatagramCrypto.NewKey());
        var plain = new byte[64];
        var datagram = new byte[64];
        var length = tx.Seal("click"u8, datagram);

        var tampered = datagram[..length].ToArray();
        tampered[DatagramCrypto.HeaderSize] ^= 1;
        Assert.False(rx.TryOpen(tampered, plain, out _));

        Assert.True(rx.TryOpen(datagram.AsSpan(0, length), plain, out _));
        Assert.False(rx.TryOpen(datagram.AsSpan(0, length), plain, out _)); // replay

        var foreign = new byte[64];
        var foreignLength = other.Seal("click"u8, foreign);
        Assert.False(rx.TryOpen(foreign.AsSpan(0, foreignLength), plain, out _));
    }

    [Fact]
    public void OutOfOrderPacketsWithinWindowAreAccepted()
    {
        var key = DatagramCrypto.NewKey();
        using var tx = new DatagramCrypto(9, key);
        using var rx = new DatagramCrypto(9, key);
        var datagrams = Enumerable.Range(0, 10).Select(_ =>
        {
            var d = new byte[64];
            var l = tx.Seal("x"u8, d);
            return d[..l];
        }).ToList();
        var plain = new byte[64];
        Assert.True(rx.TryOpen(datagrams[9], plain, out _));
        Assert.True(rx.TryOpen(datagrams[3], plain, out _));
        Assert.False(rx.TryOpen(datagrams[3], plain, out _));
        Assert.True(rx.TryOpen(datagrams[0], plain, out _));
    }
}

public class PairingCodeTests
{
    [Fact]
    public void BothSidesComputeTheSameCode()
    {
        var ni = PairingCode.NewNonce();
        var nr = PairingCode.NewNonce();
        var a = PairingCode.Compute("aa", "bb", ni, nr);
        var b = PairingCode.Compute("aa", "bb", ni, nr);
        Assert.Equal(a, b);
        Assert.Equal(6, a.Length);
        Assert.All(a, c => Assert.True(char.IsDigit(c)));
    }

    [Fact]
    public void DifferentCertificatesOrNoncesGiveDifferentCodes()
    {
        var ni = PairingCode.NewNonce();
        var nr = PairingCode.NewNonce();
        var codes = new HashSet<string>
        {
            PairingCode.Compute("aa", "bb", ni, nr),
            PairingCode.Compute("aa", "cc", ni, nr),
            PairingCode.Compute("aa", "bb", PairingCode.NewNonce(), nr),
        };
        Assert.Equal(3, codes.Count);
    }

    [Fact]
    public void CommitmentOnlyMatchesTheCommittedNonce()
    {
        var nonce = PairingCode.NewNonce();
        var commitment = PairingCode.Commit(nonce);
        Assert.True(PairingCode.VerifyCommitment(commitment, nonce));
        Assert.False(PairingCode.VerifyCommitment(commitment, PairingCode.NewNonce()));
    }
}

public class PacketFuzzTests
{
    [Fact]
    public void RandomBytesNeverThrow()
    {
        var random = new Random(1234);
        var buffer = new byte[600];
        for (var i = 0; i < 20_000; i++)
        {
            var length = random.Next(0, buffer.Length);
            random.NextBytes(buffer.AsSpan(0, length));
            InputPacket.TryDecode(buffer.AsSpan(0, length), out _);
        }
    }

    [Fact]
    public void MutatedValidPacketsNeverThrow()
    {
        var packet = new InputPacket { Ack = 9, Motion = new MotionStamp(1, 2, 3), Ping = 5 };
        packet.Items.Add(new SequencedItem(1, default, InputItem.FromEvent(InputEvent.KeyEvent(1, 2, false, true))));
        packet.Items.Add(new SequencedItem(2, default, InputItem.Enter(Layout.Side.Right, 0.3)));
        var buffer = new byte[512];
        var length = packet.Encode(buffer);
        var random = new Random(99);
        for (var i = 0; i < 20_000; i++)
        {
            var copy = buffer[..length];
            copy[random.Next(length)] = (byte)random.Next(256);
            InputPacket.TryDecode(copy, out _);
        }
    }

    [Fact]
    public void DecodingIntoAReusedPacketClearsOldContent()
    {
        var first = new InputPacket { Ack = 1, Motion = new MotionStamp(1, 1, 1), Ping = 7 };
        first.Items.Add(new SequencedItem(1, default, InputItem.TakeBack()));
        var second = new InputPacket { Ack = 2 };
        var a = new byte[256];
        var b = new byte[256];
        var la = first.Encode(a);
        var lb = second.Encode(b);

        var reused = new InputPacket();
        Assert.True(InputPacket.TryDecodeInto(a.AsSpan(0, la), reused));
        Assert.True(InputPacket.TryDecodeInto(b.AsSpan(0, lb), reused));
        Assert.Equal(2u, reused.Ack);
        Assert.Null(reused.Motion);
        Assert.Null(reused.Ping);
        Assert.Empty(reused.Items);
    }
}

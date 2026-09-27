namespace LanConnect.Core.Net;

/// <summary>
/// Sending half of the input stream. Movement is lossy-but-lossless (cumulative totals);
/// keys, clicks, scrolls and control events are resent in every packet until acknowledged.
/// Not thread-safe: owned by one sender loop.
/// </summary>
public sealed class InputStreamSender
{
    private readonly List<SequencedItem> _unacked = new();
    private uint _nextSeq = 1;
    private MotionStamp _motion;
    private bool _motionDirty;

    public MotionStamp Motion => _motion;

    public int UnackedCount => _unacked.Count;

    public bool HasPending => _motionDirty || _unacked.Count > 0;

    public bool MotionDirty => _motionDirty;

    public void AddMotion(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }
        _motion = new MotionStamp(unchecked(_motion.Seq + 1), unchecked(_motion.X + dx), unchecked(_motion.Y + dy));
        _motionDirty = true;
    }

    public uint Enqueue(InputItem item)
    {
        var seq = _nextSeq;
        _nextSeq = unchecked(_nextSeq + 1);
        _unacked.Add(new SequencedItem(seq, _motion, item));
        return seq;
    }

    public void Acknowledge(uint ack)
    {
        var removed = 0;
        while (removed < _unacked.Count && (int)(_unacked[removed].Seq - ack) <= 0)
        {
            removed++;
        }
        if (removed > 0)
        {
            _unacked.RemoveRange(0, removed);
        }
    }

    /// <summary>Fills a packet with the current motion total and the oldest unacknowledged items.</summary>
    public void Fill(InputPacket packet)
    {
        packet.Motion = _motion;
        packet.Items.Clear();
        var count = Math.Min(_unacked.Count, InputPacket.MaxItems);
        for (var i = 0; i < count; i++)
        {
            packet.Items.Add(_unacked[i]);
        }
        _motionDirty = false;
    }

    /// <summary>Drops everything (connection lost).</summary>
    public void Clear()
    {
        _unacked.Clear();
        _motionDirty = false;
    }
}

/// <summary>What the receiving side should do, in order.</summary>
public readonly record struct ReceivedInput(bool IsMotion, int Dx, int Dy, InputItem Item);

/// <summary>
/// Receiving half of the input stream. Delivers each reliable item exactly once and in order,
/// and applies the movement that happened before an item first, so a click lands exactly where
/// it was made on the controlling computer.
/// </summary>
public sealed class InputStreamReceiver
{
    private readonly SortedDictionary<uint, SequencedItem> _outOfOrder = new(new WrappingComparer());
    private uint _delivered;
    // Both ends start a fresh stream at zero, so no movement is lost before the first packet.
    private MotionStamp _lastMotion;

    /// <summary>Acknowledgement to send back: everything up to here was delivered.</summary>
    public uint Ack => _delivered;

    public void Receive(InputPacket packet, List<ReceivedInput> output)
    {
        foreach (var item in packet.Items)
        {
            if ((int)(item.Seq - _delivered) <= 0)
            {
                continue; // duplicate
            }
            _outOfOrder.TryAdd(item.Seq, item);
        }

        while (_outOfOrder.Remove(unchecked(_delivered + 1), out var next))
        {
            MotionUpTo(next.At, output);
            output.Add(new ReceivedInput(false, 0, 0, next.Item));
            _delivered = next.Seq;
        }

        if (packet.Motion is { } motion && _outOfOrder.Count == 0)
        {
            // Only move ahead when nothing is missing, otherwise a pending click would end up
            // after movement that happened later.
            MotionUpTo(motion, output);
        }
    }

    private void MotionUpTo(MotionStamp stamp, List<ReceivedInput> output)
    {
        var last = _lastMotion;
        if (!stamp.IsNewerThan(last))
        {
            return;
        }
        var dx = unchecked(stamp.X - last.X);
        var dy = unchecked(stamp.Y - last.Y);
        _lastMotion = stamp;
        if (dx != 0 || dy != 0)
        {
            output.Add(new ReceivedInput(true, dx, dy, default));
        }
    }

    private sealed class WrappingComparer : IComparer<uint>
    {
        public int Compare(uint x, uint y) => (int)(x - y);
    }
}

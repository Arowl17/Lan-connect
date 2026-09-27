using System.Buffers.Binary;
using LanConnect.Core.Input;
using LanConnect.Core.Layout;

namespace LanConnect.Core.Net;

public enum InputItemType : byte
{
    Key = 1,
    Button = 2,
    Wheel = 3,

    /// <summary>Controller → controlled: the cursor enters your desktop on <c>Side</c> at <c>Fraction</c>.</summary>
    Enter = 10,

    /// <summary>Controlled → controller: the cursor left my desktop through <c>Side</c> at <c>Fraction</c>.</summary>
    Leave = 11,

    /// <summary>Controller → controlled: control was taken back (hotkey); release everything.</summary>
    TakeBack = 12,
}

/// <summary>An event that must arrive exactly once and in order.</summary>
public readonly record struct InputItem(InputItemType Type, InputEvent Event = default, Side Side = default, double Fraction = 0)
{
    public static InputItem FromEvent(in InputEvent e) => e.Kind switch
    {
        InputEventKind.Key => new InputItem(InputItemType.Key, e),
        InputEventKind.MouseButton => new InputItem(InputItemType.Button, e),
        InputEventKind.MouseWheel => new InputItem(InputItemType.Wheel, e),
        _ => throw new ArgumentException("Mouse movement is not a reliable item.", nameof(e)),
    };

    public static InputItem Enter(Side side, double fraction) => new(InputItemType.Enter, Side: side, Fraction: fraction);

    public static InputItem Leave(Side side, double fraction) => new(InputItemType.Leave, Side: side, Fraction: fraction);

    public static InputItem TakeBack() => new(InputItemType.TakeBack);
}

/// <summary>
/// Cumulative mouse movement since the stream started. Sending totals instead of deltas means a
/// lost packet never loses movement: the next packet simply carries the new total.
/// </summary>
public readonly record struct MotionStamp(uint Seq, int X, int Y)
{
    public bool IsNewerThan(MotionStamp other) => (int)(Seq - other.Seq) > 0;
}

public readonly record struct SequencedItem(uint Seq, MotionStamp At, InputItem Item);

[Flags]
public enum PacketFlags : byte
{
    None = 0,
    HasMotion = 1,
    Ping = 2,
    Pong = 4,
}

public sealed class InputPacket
{
    public const int MaxItems = 32;

    /// <summary>Highest reliable sequence number received in order from the peer.</summary>
    public uint Ack { get; set; }

    public MotionStamp? Motion { get; set; }

    public long? Ping { get; set; }

    public long? Pong { get; set; }

    public List<SequencedItem> Items { get; } = new();

    public int Encode(Span<byte> buffer)
    {
        var w = new SpanWriter(buffer);
        var flags = PacketFlags.None;
        if (Motion is not null) flags |= PacketFlags.HasMotion;
        if (Ping is not null) flags |= PacketFlags.Ping;
        if (Pong is not null) flags |= PacketFlags.Pong;
        w.U8((byte)flags);
        w.U32(Ack);
        if (Motion is { } m) WriteStamp(ref w, m);
        if (Ping is { } ping) w.I64(ping);
        if (Pong is { } pong) w.I64(pong);
        var count = Math.Min(Items.Count, MaxItems);
        w.U8((byte)count);
        for (var i = 0; i < count; i++)
        {
            var s = Items[i];
            w.U32(s.Seq);
            WriteStamp(ref w, s.At);
            var item = s.Item;
            w.U8((byte)item.Type);
            switch (item.Type)
            {
                case InputItemType.Key:
                    w.U16(item.Event.ScanCode);
                    w.U16(item.Event.VirtualKey);
                    w.U8((byte)((item.Event.Down ? 1 : 0) | (item.Event.Extended ? 2 : 0)));
                    break;
                case InputItemType.Button:
                    w.U8((byte)item.Event.Button);
                    w.U8(item.Event.Down ? (byte)1 : (byte)0);
                    break;
                case InputItemType.Wheel:
                    w.I32(item.Event.WheelDelta);
                    w.U8(item.Event.Horizontal ? (byte)1 : (byte)0);
                    break;
                case InputItemType.Enter:
                case InputItemType.Leave:
                    w.U8((byte)item.Side);
                    w.U16(EncodeFraction(item.Fraction));
                    break;
            }
        }
        return w.Position;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out InputPacket packet)
    {
        packet = new InputPacket();
        return TryDecodeInto(data, packet);
    }

    /// <summary>Decodes into an existing packet (reused by the receive path to avoid allocations).</summary>
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, InputPacket packet)
    {
        packet.Items.Clear();
        packet.Motion = null;
        packet.Ping = null;
        packet.Pong = null;
        {
            var r = new SpanReader(data);
            var flags = (PacketFlags)r.U8();
            packet.Ack = r.U32();
            if (flags.HasFlag(PacketFlags.HasMotion)) packet.Motion = ReadStamp(ref r);
            if (flags.HasFlag(PacketFlags.Ping)) packet.Ping = r.I64();
            if (flags.HasFlag(PacketFlags.Pong)) packet.Pong = r.I64();
            int count = r.U8();
            if (r.Failed || count > MaxItems)
            {
                return false;
            }
            for (var i = 0; i < count; i++)
            {
                var seq = r.U32();
                var at = ReadStamp(ref r);
                var type = (InputItemType)r.U8();
                InputItem item;
                switch (type)
                {
                    case InputItemType.Key:
                        {
                            var scan = r.U16();
                            var vk = r.U16();
                            var f = r.U8();
                            item = new InputItem(type, InputEvent.KeyEvent(scan, vk, (f & 2) != 0, (f & 1) != 0));
                            break;
                        }
                    case InputItemType.Button:
                        {
                            var button = (MouseButton)r.U8();
                            if (button > MouseButton.X2) return false;
                            item = new InputItem(type, InputEvent.MouseButtonEvent(button, r.U8() != 0));
                            break;
                        }
                    case InputItemType.Wheel:
                        {
                            var delta = r.I32();
                            item = new InputItem(type, InputEvent.Wheel(delta, r.U8() != 0));
                            break;
                        }
                    case InputItemType.Enter:
                    case InputItemType.Leave:
                        {
                            var side = (Side)r.U8();
                            if (side > Side.Bottom) return false;
                            item = new InputItem(type, Side: side, Fraction: DecodeFraction(r.U16()));
                            break;
                        }
                    case InputItemType.TakeBack:
                        item = new InputItem(type);
                        break;
                    default:
                        return false;
                }
                if (r.Failed)
                {
                    return false;
                }
                packet.Items.Add(new SequencedItem(seq, at, item));
            }
            return !r.Failed;
        }
    }

    private static void WriteStamp(ref SpanWriter w, MotionStamp s)
    {
        w.U32(s.Seq);
        w.I32(s.X);
        w.I32(s.Y);
    }

    private static MotionStamp ReadStamp(ref SpanReader r) => new(r.U32(), r.I32(), r.I32());

    private static ushort EncodeFraction(double f) => (ushort)Math.Round(Math.Clamp(double.IsFinite(f) ? f : 0.5, 0, 1) * ushort.MaxValue);

    private static double DecodeFraction(ushort v) => v / (double)ushort.MaxValue;

    private ref struct SpanWriter(Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;

        public int Position { get; private set; }

        public void U8(byte v) => _buffer[Position++] = v;

        public void U16(ushort v) { BinaryPrimitives.WriteUInt16BigEndian(_buffer[Position..], v); Position += 2; }

        public void U32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(_buffer[Position..], v); Position += 4; }

        public void I32(int v) { BinaryPrimitives.WriteInt32BigEndian(_buffer[Position..], v); Position += 4; }

        public void I64(long v) { BinaryPrimitives.WriteInt64BigEndian(_buffer[Position..], v); Position += 8; }
    }

    /// <summary>Reads big-endian values; past the end it returns zeros and sets <see cref="Failed"/>.</summary>
    private ref struct SpanReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public bool Failed { get; private set; }

        public byte U8()
        {
            var span = Take(1);
            return span.IsEmpty ? (byte)0 : span[0];
        }

        public ushort U16()
        {
            var span = Take(2);
            return span.IsEmpty ? (ushort)0 : BinaryPrimitives.ReadUInt16BigEndian(span);
        }

        public uint U32()
        {
            var span = Take(4);
            return span.IsEmpty ? 0 : BinaryPrimitives.ReadUInt32BigEndian(span);
        }

        public int I32()
        {
            var span = Take(4);
            return span.IsEmpty ? 0 : BinaryPrimitives.ReadInt32BigEndian(span);
        }

        public long I64()
        {
            var span = Take(8);
            return span.IsEmpty ? 0 : BinaryPrimitives.ReadInt64BigEndian(span);
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (Failed || _position + count > _data.Length)
            {
                Failed = true;
                return default;
            }
            var slice = _data.Slice(_position, count);
            _position += count;
            return slice;
        }
    }
}

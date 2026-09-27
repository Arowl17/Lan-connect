using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LanConnect.Core.Net;

/// <summary>
/// AES-256-GCM sealing for input datagrams. Each direction has its own random key (sent over the
/// TLS control connection), the 64-bit packet counter is the nonce, and a sliding window rejects
/// replayed packets.
/// Layout: [u32 session id][u64 counter][ciphertext][16-byte tag].
/// </summary>
public sealed class DatagramCrypto : IDisposable
{
    public const int KeySize = 32;
    public const int HeaderSize = 12;
    public const int TagSize = 16;
    public const int Overhead = HeaderSize + TagSize;

    private readonly AesGcm _aes;
    private ulong _sendCounter;
    private ulong _highestReceived;
    private ulong _window;

    public DatagramCrypto(uint sessionId, byte[] key)
    {
        SessionId = sessionId;
        _aes = new AesGcm(key, TagSize);
    }

    public uint SessionId { get; }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static uint NewSessionId()
    {
        uint id;
        do
        {
            id = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
        }
        while (id == 0);
        return id;
    }

    public static bool TryReadSessionId(ReadOnlySpan<byte> datagram, out uint sessionId)
    {
        sessionId = 0;
        if (datagram.Length < Overhead)
        {
            return false;
        }
        sessionId = BinaryPrimitives.ReadUInt32BigEndian(datagram);
        return true;
    }

    /// <summary>Encrypts <paramref name="plaintext"/> into <paramref name="output"/>; returns the datagram length.</summary>
    public int Seal(ReadOnlySpan<byte> plaintext, Span<byte> output)
    {
        var counter = Interlocked.Increment(ref _sendCounter);
        BinaryPrimitives.WriteUInt32BigEndian(output, SessionId);
        BinaryPrimitives.WriteUInt64BigEndian(output[4..], counter);
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
        var header = output[..HeaderSize];
        var cipher = output.Slice(HeaderSize, plaintext.Length);
        var tag = output.Slice(HeaderSize + plaintext.Length, TagSize);
        _aes.Encrypt(nonce, plaintext, cipher, tag, header);
        return HeaderSize + plaintext.Length + TagSize;
    }

    /// <summary>Decrypts and authenticates; rejects forgeries and replays.</summary>
    public bool TryOpen(ReadOnlySpan<byte> datagram, Span<byte> plaintext, out int length)
    {
        length = 0;
        if (datagram.Length < Overhead || BinaryPrimitives.ReadUInt32BigEndian(datagram) != SessionId)
        {
            return false;
        }
        var counter = BinaryPrimitives.ReadUInt64BigEndian(datagram[4..]);
        if (counter == 0 || IsReplay(counter))
        {
            return false;
        }
        length = datagram.Length - Overhead;
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
        try
        {
            _aes.Decrypt(nonce, datagram.Slice(HeaderSize, length), datagram.Slice(HeaderSize + length, TagSize), plaintext[..length], datagram[..HeaderSize]);
        }
        catch (CryptographicException)
        {
            length = 0;
            return false;
        }
        MarkReceived(counter);
        return true;
    }

    private bool IsReplay(ulong counter)
    {
        if (counter > _highestReceived)
        {
            return false;
        }
        var age = _highestReceived - counter;
        return age >= 64 || (_window & (1UL << (int)age)) != 0;
    }

    private void MarkReceived(ulong counter)
    {
        if (counter > _highestReceived)
        {
            var shift = counter - _highestReceived;
            _window = shift >= 64 ? 1UL : (_window << (int)shift) | 1UL;
            _highestReceived = counter;
        }
        else
        {
            _window |= 1UL << (int)(_highestReceived - counter);
        }
    }

    public void Dispose() => _aes.Dispose();
}

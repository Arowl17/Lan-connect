using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace LanConnect.Core.Net;

public enum FrameType : byte
{
    Control = 1,

    /// <summary>An encrypted input datagram carried over TCP when UDP is blocked.</summary>
    InputTunnel = 2,
}

/// <summary>
/// A mutually authenticated TLS connection carrying length-prefixed frames:
/// [u32 length][u8 type][payload]. Both sides present their device certificate; callers decide
/// whether the peer's fingerprint is acceptable.
/// </summary>
public sealed class FramedConnection : IAsyncDisposable
{
    public const int DefaultPort = 47810;
    private const int MaxFrameSize = 4 * 1024 * 1024;

    private readonly TcpClient _tcp;
    private readonly SslStream _ssl;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    private FramedConnection(TcpClient tcp, SslStream ssl, bool isInitiator)
    {
        _tcp = tcp;
        _ssl = ssl;
        IsInitiator = isInitiator;
        PeerFingerprint = DeviceIdentity.FingerprintOf(ssl.RemoteCertificate!);
        RemoteEndPoint = (IPEndPoint)tcp.Client.RemoteEndPoint!;
    }

    public bool IsInitiator { get; }

    public string PeerFingerprint { get; }

    public IPEndPoint RemoteEndPoint { get; }

    public static async Task<FramedConnection> ConnectAsync(IPEndPoint endPoint, DeviceIdentity identity, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var tcp = new TcpClient(endPoint.AddressFamily) { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(endPoint, cts.Token).ConfigureAwait(false);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "lan-connect",
                ClientCertificateContext = SslStreamCertificateContext.Create(identity.Certificate, null, offline: true),
                // Self-signed by design: trust is established by pinning fingerprints, checked by the caller.
                RemoteCertificateValidationCallback = static (_, certificate, _, _) => certificate is not null,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EnabledSslProtocols = SslProtocols.None,
            }, cts.Token).ConfigureAwait(false);
            return new FramedConnection(tcp, ssl, isInitiator: true);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public static async Task<FramedConnection> AcceptAsync(TcpClient tcp, DeviceIdentity identity, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        tcp.NoDelay = true;
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = SslStreamCertificateContext.Create(identity.Certificate, null, offline: true),
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = static (_, certificate, _, _) => certificate is not null,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EnabledSslProtocols = SslProtocols.None,
            }, cts.Token).ConfigureAwait(false);
            return new FramedConnection(tcp, ssl, isInitiator: false);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            tcp.Dispose();
            throw;
        }
    }

    public Task SendAsync(ControlMessage message, CancellationToken cancellationToken = default) =>
        SendFrameAsync(FrameType.Control, ControlJson.Serialize(message), cancellationToken);

    public async Task SendFrameAsync(FrameType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length > MaxFrameSize)
        {
            throw new ArgumentException("Frame too large.", nameof(payload));
        }
        var buffer = ArrayPool<byte>.Shared.Rent(payload.Length + 5);
        try
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)(payload.Length + 1));
            buffer[4] = (byte)type;
            payload.Span.CopyTo(buffer.AsSpan(5));
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _ssl.WriteAsync(buffer.AsMemory(0, payload.Length + 5), cancellationToken).ConfigureAwait(false);
                await _ssl.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads the next frame; returns null when the connection closed cleanly.</summary>
    public async Task<(FrameType Type, byte[] Payload)?> ReceiveFrameAsync(CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > MaxFrameSize + 1)
        {
            throw new InvalidDataException($"Invalid frame length {length}.");
        }
        var body = new byte[length];
        if (!await ReadExactlyOrEndAsync(body, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return ((FrameType)body[0], body[1..]);
    }

    /// <summary>Reads frames until the next control message (tunnelled input is returned via <paramref name="onTunnel"/>).</summary>
    public async Task<ControlMessage?> ReceiveControlAsync(CancellationToken cancellationToken, Action<byte[]>? onTunnel = null)
    {
        while (true)
        {
            var frame = await ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame is not { } f)
            {
                return null;
            }
            switch (f.Type)
            {
                case FrameType.Control:
                    return ControlJson.Deserialize(f.Payload) ?? throw new InvalidDataException("Empty control message.");
                case FrameType.InputTunnel:
                    onTunnel?.Invoke(f.Payload);
                    break;
                default:
                    // Unknown frame types from newer versions are skipped.
                    break;
            }
        }
    }

    private async Task<bool> ReadExactlyOrEndAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await _ssl.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0)
                {
                    return false;
                }
                throw new EndOfStreamException();
            }
            read += n;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            await _ssl.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // already broken
        }
        _tcp.Dispose();
        _writeLock.Dispose();
    }
}

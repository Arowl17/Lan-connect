using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LanConnect.Core.Net;

/// <summary>
/// Six-digit pairing code both screens show, like Bluetooth "numeric comparison".
///
/// The responder commits to its random nonce before seeing the initiator's nonce, and the code
/// is derived from both nonces and both certificate fingerprints seen in the TLS session. A
/// man-in-the-middle would have to guess the code in advance (1 in a million per attempt),
/// so if both screens show the same number, the two computers are really talking to each other.
/// </summary>
public static class PairingCode
{
    public const int NonceSize = 32;

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceSize);

    public static byte[] Commit(ReadOnlySpan<byte> responderNonce)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("lan-connect/commit/v1"u8);
        hash.AppendData(responderNonce);
        return hash.GetHashAndReset();
    }

    public static bool VerifyCommitment(ReadOnlySpan<byte> commitment, ReadOnlySpan<byte> revealedNonce) =>
        revealedNonce.Length == NonceSize && CryptographicOperations.FixedTimeEquals(commitment, Commit(revealedNonce));

    public static string Compute(string initiatorFingerprint, string responderFingerprint, ReadOnlySpan<byte> initiatorNonce, ReadOnlySpan<byte> responderNonce)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("lan-connect/sas/v1"u8);
        hash.AppendData(Encoding.ASCII.GetBytes(initiatorFingerprint));
        hash.AppendData(Encoding.ASCII.GetBytes(responderFingerprint));
        hash.AppendData(initiatorNonce);
        hash.AppendData(responderNonce);
        var value = BinaryPrimitives.ReadUInt32BigEndian(hash.GetHashAndReset()) % 1_000_000;
        return value.ToString("D6");
    }

    /// <summary>"482913" → "482 913" for display.</summary>
    public static string Format(string code) => code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;
}

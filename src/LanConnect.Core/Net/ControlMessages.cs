using System.Text.Json;
using System.Text.Json.Serialization;
using LanConnect.Core.Layout;

namespace LanConnect.Core.Net;

public enum ConnectPurpose
{
    Session,
    Pair,
}

/// <summary>Messages on the encrypted TCP control connection.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(PairCommitMessage), "pair-commit")]
[JsonDerivedType(typeof(PairNonceMessage), "pair-nonce")]
[JsonDerivedType(typeof(PairRevealMessage), "pair-reveal")]
[JsonDerivedType(typeof(PairDecisionMessage), "pair-decision")]
[JsonDerivedType(typeof(SessionKeyMessage), "session-key")]
[JsonDerivedType(typeof(PlacementMessage), "placement")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(UnpairMessage), "unpair")]
public abstract record ControlMessage;

public sealed record HelloMessage(int ProtocolVersion, string DeviceName, ConnectPurpose Purpose, int UdpPort, string AppVersion) : ControlMessage;

public sealed record PairCommitMessage(byte[] Commitment) : ControlMessage;

public sealed record PairNonceMessage(byte[] Nonce) : ControlMessage;

public sealed record PairRevealMessage(byte[] Nonce) : ControlMessage;

public sealed record PairDecisionMessage(bool Accepted) : ControlMessage;

/// <summary>"When you send input to me over UDP, use this session id and key."</summary>
public sealed record SessionKeyMessage(uint SessionId, byte[] Key) : ControlMessage;

/// <summary>"You are on my <paramref name="YouAreOnMy"/> side" (null = not placed).</summary>
public sealed record PlacementMessage(Side? YouAreOnMy) : ControlMessage;

public sealed record PingMessage(long Timestamp) : ControlMessage;

public sealed record PongMessage(long Timestamp) : ControlMessage;

public sealed record UnpairMessage : ControlMessage;

internal static class ControlJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Serialize(ControlMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    public static ControlMessage? Deserialize(ReadOnlySpan<byte> json) => JsonSerializer.Deserialize<ControlMessage>(json, Options);
}

using System.Security.Cryptography;

namespace LanConnect.Core.Net;

public interface ITrustStore
{
    bool IsTrusted(string fingerprint);

    /// <summary>Remember a newly paired computer.</summary>
    void Trust(string fingerprint, string name);
}

/// <summary>
/// One pairing attempt, shown to the user as "Confirm that both screens show 482 913".
/// Pairing succeeds only when the users on both computers confirm.
/// </summary>
public sealed class PairingSession
{
    private readonly FramedConnection _connection;
    private readonly TaskCompletionSource<bool> _localDecision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PairingSession(FramedConnection connection, string peerName, string code, bool isInitiator)
    {
        _connection = connection;
        PeerId = connection.PeerFingerprint;
        PeerName = peerName;
        Code = code;
        IsInitiator = isInitiator;
    }

    public string PeerId { get; }

    public string PeerName { get; }

    /// <summary>Six digits, identical on both screens when nobody is interfering.</summary>
    public string Code { get; }

    public bool IsInitiator { get; }

    /// <summary>Completes with true when both sides confirmed, false otherwise.</summary>
    public Task<bool> Result => _result.Task;

    public void Confirm() => _localDecision.TrySetResult(true);

    public void Reject() => _localDecision.TrySetResult(false);

    private static readonly TimeSpan DecisionTimeout = TimeSpan.FromMinutes(2);

    internal static async Task<PairingSession> RunInitiatorAsync(FramedConnection connection, string ownFingerprint, string peerName, CancellationToken token)
    {
        var commit = await ExpectAsync<PairCommitMessage>(connection, token).ConfigureAwait(false);
        var ownNonce = PairingCode.NewNonce();
        await connection.SendAsync(new PairNonceMessage(ownNonce), token).ConfigureAwait(false);
        var reveal = await ExpectAsync<PairRevealMessage>(connection, token).ConfigureAwait(false);
        if (!PairingCode.VerifyCommitment(commit.Commitment, reveal.Nonce))
        {
            throw new CryptographicException("Pairing commitment mismatch.");
        }
        var code = PairingCode.Compute(ownFingerprint, connection.PeerFingerprint, ownNonce, reveal.Nonce);
        return new PairingSession(connection, peerName, code, isInitiator: true);
    }

    internal static async Task<PairingSession> RunResponderAsync(FramedConnection connection, string ownFingerprint, string peerName, CancellationToken token)
    {
        var ownNonce = PairingCode.NewNonce();
        await connection.SendAsync(new PairCommitMessage(PairingCode.Commit(ownNonce)), token).ConfigureAwait(false);
        var peerNonce = await ExpectAsync<PairNonceMessage>(connection, token).ConfigureAwait(false);
        if (peerNonce.Nonce.Length != PairingCode.NonceSize)
        {
            throw new CryptographicException("Invalid pairing nonce.");
        }
        await connection.SendAsync(new PairRevealMessage(ownNonce), token).ConfigureAwait(false);
        var code = PairingCode.Compute(connection.PeerFingerprint, ownFingerprint, peerNonce.Nonce, ownNonce);
        return new PairingSession(connection, peerName, code, isInitiator: false);
    }

    /// <summary>Exchanges both users' decisions, records the trust on success, closes the connection.</summary>
    internal async Task CompleteAsync(ITrustStore trust, CancellationToken token)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(DecisionTimeout);
            var remoteDecision = ExpectAsync<PairDecisionMessage>(_connection, cts.Token);
            var local = await WaitAsync(_localDecision.Task, remoteDecision, cts.Token).ConfigureAwait(false);
            await _connection.SendAsync(new PairDecisionMessage(local), cts.Token).ConfigureAwait(false);
            if (!local)
            {
                _result.TrySetResult(false);
                return;
            }
            var remote = await remoteDecision.ConfigureAwait(false);
            if (remote.Accepted)
            {
                trust.Trust(PeerId, PeerName);
            }
            _result.TrySetResult(remote.Accepted);
        }
        catch (Exception e)
        {
            Log.Warn($"Pairing with {PeerName} did not complete: {e.Message}");
            _result.TrySetResult(false);
        }
        finally
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Waits for the local decision, but gives up early if the other side declines first.</summary>
    private static async Task<bool> WaitAsync(Task<bool> local, Task<PairDecisionMessage> remote, CancellationToken token)
    {
        var first = await Task.WhenAny(local, remote, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (first == remote && !(await remote.ConfigureAwait(false)).Accepted)
        {
            return false;
        }
        return await local.ConfigureAwait(false);
    }

    private static async Task<T> ExpectAsync<T>(FramedConnection connection, CancellationToken token) where T : ControlMessage
    {
        var message = await connection.ReceiveControlAsync(token).ConfigureAwait(false);
        return message as T ?? throw new InvalidDataException($"Expected {typeof(T).Name}, got {message?.GetType().Name ?? "end of stream"}.");
    }
}

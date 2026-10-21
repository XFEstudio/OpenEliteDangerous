namespace XFEEliteDangerous.NativeFormat.Replication;

public enum EdServerHandshakeState { Disconnected, AwaitingAcknowledgement, Established }

/// <summary>
/// A deliberately narrow, evidence-based server-side SYNC/ACK exchange. It tracks
/// one outstanding server SYNC. It does not implement replication, allocation,
/// timers, compressed letters, or imply a playable session. The owner must use
/// EdServerLetterFramingCodec for the peer's advertised framing/alignment.
/// The owner must serialize access to each machine and retransmit pending SYNCs.
/// </summary>
public sealed class EdServerHandshakeMachine
{
    private readonly EdServerSyncMessage _serverParameters;
    private ushort _lastAcknowledgedLocalSequence;
    private ushort _localSequence;
    private EdServerSyncMessage? _pendingSync;

    public EdServerHandshakeState State { get; private set; }
    public ushort RemoteSequence { get; private set; }
    public ushort LocalSequence => _localSequence;
    public int IncomingLetterTypeBits { get; private set; } = EdServerControlMessageCodec.InitialLetterTypeBits;
    public bool IncomingLengthDelimited { get; private set; }
    public byte IncomingAlignmentMode { get; private set; }
    public EdServerSyncMessage? PendingSync => _pendingSync;

    /// <summary>
    /// Native4C3F280 resets a connected machine for a different hard-SYNC
    /// sequence through4C26CA0, reseeds its sequence and waits for a new ACK.
    /// Return an independent validated candidate. Its owner must discard the
    /// previous reliable window, references and membership when committing it.
    /// This method does not mutate or silently reuse the old application.
    /// </summary>
    public EdServerHandshakeMachine CreateRestart(EdServerSyncMessage incoming,ushort newSequenceSeed)
    {
        if(State is not(EdServerHandshakeState.Established or EdServerHandshakeState.AwaitingAcknowledgement)
            ||incoming.Acknowledgement!=0||incoming.Sequence==RemoteSequence)
            throw new InvalidOperationException("A restart requires a different hard SYNC on an existing exchange.");
        var candidate=new EdServerHandshakeMachine(_serverParameters,newSequenceSeed);
        candidate.ReceiveSync(incoming);
        return candidate;
    }

    /// <param name="serverParameters">Local advertised parameters, including the exact protocol/build revisions.</param>
    /// <param name="lastAcknowledgedLocalSequence">Nonzero sequence seed; native sequence space is 1..65535.</param>
    public EdServerHandshakeMachine(EdServerSyncMessage serverParameters, ushort lastAcknowledgedLocalSequence)
    {
        ArgumentNullException.ThrowIfNull(serverParameters);
        if (lastAcknowledgedLocalSequence == 0)
            throw new ArgumentOutOfRangeException(nameof(lastAcknowledgedLocalSequence));
        if (serverParameters.IsClient || serverParameters.LetterTypeBits is < 3 or > 16
            || !serverParameters.EncryptedKeyBytes.IsEmpty)
            throw new ArgumentException("This handshake machine supports server mode3 without mode2 key exchange.", nameof(serverParameters));
        _serverParameters = serverParameters;
        _lastAcknowledgedLocalSequence = _localSequence = lastAcknowledgedLocalSequence;
    }

    /// <summary>
    /// Accepts a parsed client SYNC and returns the native server-side SYNC reply.
    /// A duplicate hard SYNC reuses the pending sequence; it does not establish.
    /// Sequence zero is a native disconnect/error advertisement, never success.
    /// </summary>
    public EdServerSyncMessage? ReceiveSync(EdServerSyncMessage incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (incoming.Protocol != _serverParameters.Protocol || !incoming.IsClient
            || incoming.BuildRevision != _serverParameters.BuildRevision
            || incoming.BaseRevision != _serverParameters.BaseRevision)
            throw new InvalidDataException("The SYNC protocol, peer role or build revisions do not match this local session.");
        if (incoming.Sequence == 0)
            throw new InvalidDataException("The client advertised a disconnected/error SYNC.");
        if (incoming.LetterTypeBits is < 1 or > 16)
            throw new InvalidDataException("The peer's letter type width is outside the verified range.");
        if (!incoming.EncryptedKeyBytes.IsEmpty)
            throw new NotSupportedException("Mode2 key exchange is not implemented.");
        if (incoming.Acknowledgement != 0)
        {
            if (State != EdServerHandshakeState.AwaitingAcknowledgement
                || incoming.Acknowledgement != Next(_lastAcknowledgedLocalSequence))
                throw new InvalidDataException("The soft SYNC acknowledgement is outside the outstanding exchange.");
            _lastAcknowledgedLocalSequence = incoming.Acknowledgement;
            RemoteSequence = incoming.Sequence;
            IncomingLetterTypeBits = incoming.LetterTypeBits;
            IncomingLengthDelimited = incoming.LengthDelimited;
            IncomingAlignmentMode = incoming.AlignmentMode;
            _pendingSync = null;
            State = EdServerHandshakeState.Established;
            return null;
        }
        else if (State == EdServerHandshakeState.Disconnected)
        {
            _localSequence = Next(_localSequence);
            State = EdServerHandshakeState.AwaitingAcknowledgement;
        }
        else if (State == EdServerHandshakeState.Established && incoming.Sequence != RemoteSequence)
        {
            // The full native reset/reconnection path needs its own state model.
            throw new NotSupportedException("A new hard SYNC after establishment requires the reconnect state machine.");
        }
        RemoteSequence = incoming.Sequence;
        IncomingLetterTypeBits = incoming.LetterTypeBits;
        IncomingLengthDelimited = incoming.LengthDelimited;
        IncomingAlignmentMode = incoming.AlignmentMode;
        _pendingSync = _serverParameters with
        {
            Sequence = _localSequence,
            Acknowledgement = RemoteSequence
        };
        return _pendingSync;
    }

    /// <summary>Only an ACK for this exchange's outstanding server sequence establishes it.</summary>
    public bool ReceiveAcknowledgement(EdServerAckMessage acknowledgement)
    {
        if (acknowledgement.Sequence == 0 || acknowledgement.SelectiveMask > 1023 || acknowledgement.Allowance > 63)
            throw new ArgumentOutOfRangeException(nameof(acknowledgement));
        if (State != EdServerHandshakeState.AwaitingAcknowledgement || acknowledgement.Sequence != _localSequence)
            return false;
        _lastAcknowledgedLocalSequence = acknowledgement.Sequence;
        _pendingSync = null;
        State = EdServerHandshakeState.Established;
        return true;
    }

    public static ushort Next(ushort sequence)
    {
        if (sequence == 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        return sequence == ushort.MaxValue ? (ushort)1 : (ushort)(sequence + 1);
    }
}

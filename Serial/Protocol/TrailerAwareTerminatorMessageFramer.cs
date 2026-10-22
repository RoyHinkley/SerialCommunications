namespace SerialCommunications.Serial.Protocol;

/// <summary>
/// Frames a stream terminated by a reserved byte when that byte may also occur in a fixed-width trailer.
/// </summary>
/// <remarks>
/// A terminator normally establishes a message boundary whether or not the resulting frame passes its check.
/// The check is consulted only to resolve the protocol-defined ambiguity where a terminator-valued byte may
/// instead belong to the trailer. A bad frame is therefore still a frame; checking belongs downstream.
/// Each received byte is examined as a possible terminator at most once; previously rejected candidates are not
/// rescanned as later bytes arrive.
/// </remarks>
internal sealed class TrailerAwareTerminatorMessageFramer : IMessageFramer
{
    private readonly byte terminator;
    private readonly IMessageCheck check;
    private readonly List<byte> buffer = [];
    private int pendingTerminator = -1;
    private int nextTerminatorSearch;

    public TrailerAwareTerminatorMessageFramer(byte terminator, IMessageCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (check.FieldLength <= 0)
            throw new ArgumentException("Trailer-aware framing requires a non-empty fixed-width trailer.", nameof(check));

        this.terminator = terminator;
        this.check = check;
    }

    public bool HasPendingData => buffer.Count != 0;

    public void Consume(ReadOnlySpan<byte> bytes, Action<ReadOnlyMemory<byte>> frameReady)
    {
        ArgumentNullException.ThrowIfNull(frameReady);

        foreach (var value in bytes)
        {
            buffer.Add(value);
            Evaluate(frameReady);
        }
    }

    public void SilenceElapsed(Action<ReadOnlyMemory<byte>> frameReady)
    {
        ArgumentNullException.ThrowIfNull(frameReady);
        if (pendingTerminator >= 0)
            EmitAtPendingTerminator(frameReady);
    }

    public void Reset()
    {
        buffer.Clear();
        pendingTerminator = -1;
        nextTerminatorSearch = 0;
    }

    private void Evaluate(Action<ReadOnlyMemory<byte>> frameReady)
    {
        while (true)
        {
            if (pendingTerminator >= 0 && buffer.Count - pendingTerminator - 1 > check.FieldLength)
            {
                EmitAtPendingTerminator(frameReady);
                continue;
            }

            while (nextTerminatorSearch < buffer.Count)
            {
                var index = nextTerminatorSearch++;
                if (buffer[index] != terminator)
                    continue;

                if (TryEmitCandidate(index, frameReady))
                    break;

                if (pendingTerminator < 0)
                    pendingTerminator = index;
            }

            if (nextTerminatorSearch >= buffer.Count)
                return;
        }
    }

    private bool TryEmitCandidate(int terminatorIndex, Action<ReadOnlyMemory<byte>> frameReady)
    {
        if (terminatorIndex < check.FieldLength)
            return false;

        var candidate = buffer.GetRange(0, terminatorIndex).ToArray();
        if (!check.TryValidate(candidate, out var payload) || payload.Contains(terminator))
            return false;

        frameReady(candidate);
        RemoveThrough(terminatorIndex);
        return true;
    }

    private void EmitAtPendingTerminator(Action<ReadOnlyMemory<byte>> frameReady)
    {
        var frame = buffer.GetRange(0, pendingTerminator).ToArray();
        frameReady(frame);
        RemoveThrough(pendingTerminator);
    }

    private void RemoveThrough(int terminatorIndex)
    {
        var removed = terminatorIndex + 1;
        buffer.RemoveRange(0, removed);
        pendingTerminator = -1;
        nextTerminatorSearch = Math.Max(0, nextTerminatorSearch - removed);
    }
}

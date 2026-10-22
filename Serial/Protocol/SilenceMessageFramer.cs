namespace SerialCommunications.Serial.Protocol;

/// <summary>Uses an explicitly reported receive-silence interval as an inferred message boundary.</summary>
/// <remarks>
/// Silence framing is deliberately separate from receive coalescing. A transport may consolidate delivery chunks
/// for efficiency without asserting a message boundary; only <see cref="SilenceElapsed"/> completes this frame.
/// </remarks>
internal sealed class SilenceMessageFramer : IMessageFramer
{
    private readonly List<byte> pending = [];

    public bool HasPendingData => pending.Count != 0;

    public void Consume(ReadOnlySpan<byte> bytes, Action<ReadOnlyMemory<byte>> frameReady)
    {
        ArgumentNullException.ThrowIfNull(frameReady);
        foreach (var value in bytes) pending.Add(value);
    }

    public void SilenceElapsed(Action<ReadOnlyMemory<byte>> frameReady)
    {
        ArgumentNullException.ThrowIfNull(frameReady);
        if (pending.Count == 0) return;
        var frame = pending.ToArray();
        pending.Clear();
        frameReady(frame);
    }

    public void Reset() => pending.Clear();
}

namespace SerialCommunications.Serial.Protocol;

/// <summary>Frames a byte stream at an explicit terminator byte.</summary>
/// <remarks>
/// This framer is appropriate only when the terminator cannot legally occur in the frame body. Protocols where a
/// check byte may equal the terminator require a trailer-aware framer rather than special cases here.
/// </remarks>
internal sealed class TerminatorMessageFramer(byte terminator, bool includeTerminator = false) : IMessageFramer
{
    private readonly List<byte> pending = [];

    public bool HasPendingData => pending.Count != 0;

    public void Consume(ReadOnlySpan<byte> bytes, Action<ReadOnlyMemory<byte>> frameReady)
    {
        ArgumentNullException.ThrowIfNull(frameReady);
        foreach (var value in bytes)
        {
            if (value != terminator)
            {
                pending.Add(value);
                continue;
            }

            if (includeTerminator) pending.Add(value);
            Emit(frameReady);
        }
    }

    public void SilenceElapsed(Action<ReadOnlyMemory<byte>> frameReady)
    {
        ArgumentNullException.ThrowIfNull(frameReady);
    }

    public void Reset() => pending.Clear();

    private void Emit(Action<ReadOnlyMemory<byte>> frameReady)
    {
        var frame = pending.ToArray();
        pending.Clear();
        frameReady(frame);
    }
}

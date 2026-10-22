namespace SerialCommunications.Serial.Protocol;

/// <summary>
/// Incrementally identifies protocol frames without depending on how the transport partitions received bytes.
/// </summary>
/// <remarks>
/// Implementations may retain incomplete-frame state between calls. <see cref="Consume"/> must produce the same
/// frames for a given byte stream regardless of the sizes of the chunks supplied by the transport.
/// <see cref="SilenceElapsed"/> is explicit because silence is meaningful only to framers configured to use it.
/// </remarks>
internal interface IMessageFramer
{
    /// <summary>Whether bytes belonging to an incomplete frame are currently buffered.</summary>
    bool HasPendingData { get; }

    /// <summary>Consumes another contiguous portion of the received byte stream.</summary>
    void Consume(ReadOnlySpan<byte> bytes, Action<ReadOnlyMemory<byte>> frameReady);

    /// <summary>Reports an observed receive-silence boundary.</summary>
    void SilenceElapsed(Action<ReadOnlyMemory<byte>> frameReady);

    /// <summary>Discards any incomplete frame and restores initial framing state.</summary>
    void Reset();
}

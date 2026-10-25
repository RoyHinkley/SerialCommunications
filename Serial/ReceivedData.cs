namespace SerialCommunications.Serial;

/// <summary>Immutable evidence retained for one receive unit recognized by <see cref="SerialChannel"/>.</summary>
/// <remarks>
/// Wire and payload data remain exact bytes. Text decoding and other presentation choices belong to the consumer.
/// Instances are created by the receive pipeline rather than by consumers.
/// </remarks>
public sealed class ReceivedData
{
    private readonly byte[] wireBytes;
    private readonly byte[] payloadBytes;
    private readonly byte[] crcBytes;
    private readonly bool crcCheck;

    internal ReceivedData(byte[] wireBytes, byte[] payloadBytes, bool? checkValid, bool crcCheck,
        ushort? receivedCrc = null, ushort? calculatedResidue = null, ushort? expectedResidue = null,
        byte[]? crcBytes = null)
    {
        ArgumentNullException.ThrowIfNull(wireBytes);
        ArgumentNullException.ThrowIfNull(payloadBytes);
        this.wireBytes = (byte[])wireBytes.Clone();
        this.payloadBytes = (byte[])payloadBytes.Clone();
        this.crcBytes = crcBytes is null ? [] : (byte[])crcBytes.Clone();
        this.crcCheck = crcCheck;
        CheckValid = checkValid;
        ReceivedCrc = receivedCrc;
        CalculatedResidue = calculatedResidue;
        ExpectedResidue = expectedResidue;
    }

    /// <summary>The complete received frame, including configured start, end, and check elements.</summary>
    public ReadOnlyMemory<byte> WireBytes => wireBytes;

    /// <summary>The message payload P after removal of framing and check elements.</summary>
    public ReadOnlyMemory<byte> PayloadBytes => payloadBytes;

    /// <summary>Whether the configured message check validated; null when no check is configured.</summary>
    public bool? CheckValid { get; }

    /// <summary>CRC-specific alias for <see cref="CheckValid"/>; null when the configured check is not CRC-16.</summary>
    public bool? CrcValid => crcCheck ? CheckValid : null;

    /// <summary>The 16-bit CRC value carried by the message, interpreted using the configured byte order; null for non-CRC checks.</summary>
    public ushort? ReceivedCrc { get; }

    /// <summary>The CRC remainder after the complete received codeword was processed; null for non-CRC checks.</summary>
    public ushort? CalculatedResidue { get; }

    /// <summary>The residue required for a valid codeword by the active CRC configuration; null for non-CRC checks.</summary>
    public ushort? ExpectedResidue { get; }

    /// <summary>The two CRC bytes exactly as received on the wire, or an empty memory for non-CRC checks.</summary>
    public ReadOnlyMemory<byte> CrcBytes => crcBytes;
}

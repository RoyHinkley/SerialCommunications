namespace SerialCommunications.Serial;

/// <summary>Immutable evidence retained for one receive unit recognized by <see cref="SerialChannel"/>.</summary>
/// <remarks>Wire, framing segments, and payload remain exact bytes. Text decoding belongs to consumers.</remarks>
public sealed class ReceivedData
{
    private readonly byte[] wireBytes;
    private readonly byte[] prefixBytes;
    private readonly byte[] payloadBytes;
    private readonly byte[] suffixBytes;
    private readonly byte[] checkBytes;

    internal ReceivedData(byte[] wireBytes, byte[] prefixBytes, byte[] payloadBytes, byte[] suffixBytes,
        bool framingValid, bool? checkValid, int? receivedCheckCode = null, ushort? calculatedResidue = null,
        ushort? expectedResidue = null, byte[]? checkBytes = null)
    {
        ArgumentNullException.ThrowIfNull(wireBytes);
        ArgumentNullException.ThrowIfNull(prefixBytes);
        ArgumentNullException.ThrowIfNull(payloadBytes);
        ArgumentNullException.ThrowIfNull(suffixBytes);
        this.wireBytes = (byte[])wireBytes.Clone();
        this.prefixBytes = (byte[])prefixBytes.Clone();
        this.payloadBytes = (byte[])payloadBytes.Clone();
        this.suffixBytes = (byte[])suffixBytes.Clone();
        this.checkBytes = checkBytes is null ? [] : (byte[])checkBytes.Clone();
        FramingValid = framingValid;
        CheckValid = checkValid;
        ReceivedCheckCode = receivedCheckCode;
        CalculatedResidue = calculatedResidue;
        ExpectedResidue = expectedResidue;
    }

    /// <summary>Every byte received for the recognized message, including bytes preceding S and configured framing/check elements.</summary>
    public ReadOnlyMemory<byte> WireBytes => wireBytes;

    /// <summary>All wire bytes preceding payload P, including any pre-S bytes and configured S.</summary>
    public ReadOnlyMemory<byte> PrefixBytes => prefixBytes;

    /// <summary>The message payload P after removal of framing and check elements that were unambiguously recognized.</summary>
    public ReadOnlyMemory<byte> PayloadBytes => payloadBytes;

    /// <summary>All recognized wire bytes following payload P, including configured E and C in their wire order.</summary>
    public ReadOnlyMemory<byte> SuffixBytes => suffixBytes;

    /// <summary>Whether the configured framing completed successfully.</summary>
    public bool FramingValid { get; }

    /// <summary>Whether the configured message check validated; null when no check is configured.</summary>
    public bool? CheckValid { get; }

    /// <summary>The received check code interpreted according to the configured check byte order; null when unavailable.</summary>
    public int? ReceivedCheckCode { get; }

    /// <summary>The CRC remainder after the complete received codeword was processed; null for non-CRC checks.</summary>
    public ushort? CalculatedResidue { get; }

    /// <summary>The residue required for a valid codeword by the active CRC configuration; null for non-CRC checks.</summary>
    public ushort? ExpectedResidue { get; }

    /// <summary>The check bytes exactly as received on the wire, in transmission order.</summary>
    public ReadOnlyMemory<byte> CheckBytes => checkBytes;
}

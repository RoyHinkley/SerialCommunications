namespace SerialCommunications.Serial.Protocol;

/// <summary>Appends and validates a one-byte additive checksum.</summary>
/// <remarks>
/// The checksum is the low eight bits of the sum of every payload byte, starting from zero. This type operates on
/// the binary check byte only; protocol-specific representations such as two ASCII hexadecimal characters belong
/// in the protocol adapter rather than in the checksum calculation.
/// </remarks>
public sealed class ChecksumMessageCheck : IMessageCheck
{
    /// <inheritdoc />
    public int FieldLength => 1;

    /// <inheritdoc />
    public byte[] Append(ReadOnlySpan<byte> payload)
    {
        var result = new byte[payload.Length + 1];
        payload.CopyTo(result);
        result[^1] = Calculate(payload);
        return result;
    }

    /// <inheritdoc />
    public bool TryValidate(ReadOnlySpan<byte> frame, out byte[] payload)
    {
        if (frame.Length < FieldLength)
        {
            payload = [];
            return false;
        }

        payload = frame[..^1].ToArray();
        return frame[^1] == Calculate(payload);
    }

    private static byte Calculate(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var b in bytes) sum += b;
        return (byte)sum;
    }
}

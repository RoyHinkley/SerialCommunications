namespace SerialCommunications.Serial.Protocol;

/// <summary>Appends and validates a one-byte XOR check field.</summary>
/// <remarks>
/// The check byte is the exclusive OR of every payload byte, starting from zero. This is the calculation behind
/// the BCC field used by the surveyed Eurotherm protocols; BCC is a protocol field name rather than the generic
/// name of the calculation.
/// </remarks>
public sealed class XorMessageCheck : IMessageCheck
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
        byte value = 0;
        foreach (var b in bytes) value ^= b;
        return value;
    }
}

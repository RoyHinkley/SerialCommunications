namespace SerialCommunications.Serial.Protocol;

/// <summary>16-bit CRC check strategy independent of message framing.</summary>
public sealed class Crc16MessageCheck : IMessageCheck
{
    private readonly CrcOptions options;

    /// <summary>Creates a check using a snapshot of the supplied CRC options.</summary>
    public Crc16MessageCheck(CrcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = new CrcOptions(
            options.InitialValue,
            options.Polynomial,
            options.ExpectedResidue,
            options.PostInvert,
            options.MsBitFirst,
            options.MsByteFirst);
    }

    /// <summary>The two CRC bytes appended to each payload.</summary>
    public int FieldLength => 2;

    /// <inheritdoc />
    public byte[] Append(ReadOnlySpan<byte> payload) => new Crc(options).Append(payload);

    /// <inheritdoc />
    public bool TryValidate(ReadOnlySpan<byte> frame, out byte[] payload)
    {
        if (frame.Length < FieldLength)
        {
            payload = [];
            return false;
        }

        payload = frame[..^FieldLength].ToArray();
        var crc = new Crc(options);
        crc.Update(frame);
        return crc.Good();
    }
}

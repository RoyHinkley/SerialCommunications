using SerialCommunications.Serial.Protocol;

namespace SerialCommunications.Serial;

/// <summary>Adapts decoded messages to the diagnostic evidence retained by <see cref="ReceivedData"/>.</summary>
internal static class ReceivedDataAdapter
{
    public static ReceivedData Create(DecodedMessage message, SerialProtocolSettings settings)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(settings);

        var frame = message.Frame.ToArray();
        var payload = message.Payload.ToArray();
        var suffixLength = SuffixLength(settings);
        var prefixLength = frame.Length - payload.Length - suffixLength;
        if (prefixLength < 0) throw new InvalidDataException("Decoded frame is shorter than its payload and configured suffix.");
        var prefix = frame[..prefixLength];
        var suffix = frame[(prefixLength + payload.Length)..];

        if (!settings.HasCheckCode)
            return new ReceivedData(frame, prefix, payload, suffix, null);

        if (settings.CheckKind != MessageCheckKind.Crc16)
            return new ReceivedData(frame, prefix, payload, suffix, message.CheckValid);

        int? receivedCheckCode = null;
        ushort? calculatedResidue = null;
        byte[] checkBytes = [];
        var checkIndex = settings.Layout == FrameLayout.PCE
            ? frame.Length - settings.End.Length - settings.CheckCodeSize
            : frame.Length - settings.CheckCodeSize;
        if (checkIndex >= 0 && checkIndex + settings.CheckCodeSize <= frame.Length && settings.CheckCodeSize == 2)
        {
            checkBytes = frame[checkIndex..(checkIndex + settings.CheckCodeSize)];
            var first = checkBytes[0];
            var second = checkBytes[1];
            receivedCheckCode = settings.MsByteFirst ? (first << 8) | second : first | (second << 8);

            var checkedBytes = new byte[payload.Length + settings.CheckCodeSize];
            payload.CopyTo(checkedBytes, 0);
            checkBytes.CopyTo(checkedBytes, payload.Length);
            calculatedResidue = new Crc(settings.CreateCrcOptions()!).Update(checkedBytes);
        }

        return new ReceivedData(frame, prefix, payload, suffix, message.CheckValid,
            receivedCheckCode, calculatedResidue, settings.ExpectedResidue, checkBytes);
    }

    private static int SuffixLength(SerialProtocolSettings settings) => settings.Layout switch
    {
        FrameLayout.P => 0,
        FrameLayout.PE => settings.End.Length,
        FrameLayout.PC => settings.CheckCodeSize,
        FrameLayout.PEC => settings.End.Length + settings.CheckCodeSize,
        FrameLayout.SP => 0,
        FrameLayout.SPE => settings.End.Length,
        FrameLayout.SPC => settings.CheckCodeSize,
        FrameLayout.SPEC => settings.End.Length + settings.CheckCodeSize,
        FrameLayout.PCE => settings.CheckCodeSize + settings.End.Length,
        _ => throw new ArgumentOutOfRangeException(nameof(settings.Layout)),
    };
}

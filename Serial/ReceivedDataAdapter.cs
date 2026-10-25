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
        if (!settings.HasCheckCode)
            return new ReceivedData(frame, payload, null, false);

        if (settings.CheckKind != MessageCheckKind.Crc16)
            return new ReceivedData(frame, payload, message.CheckValid, false);

        ushort? receivedCrc = null;
        ushort? calculatedResidue = null;
        byte[] crcBytes = [];
        var checkIndex = settings.Layout == FrameLayout.PCE
            ? frame.Length - settings.End.Length - settings.CheckCodeSize
            : frame.Length - settings.CheckCodeSize;
        if (checkIndex >= 0 && checkIndex + settings.CheckCodeSize <= frame.Length && settings.CheckCodeSize == 2)
        {
            crcBytes = frame[checkIndex..(checkIndex + settings.CheckCodeSize)];
            var first = crcBytes[0];
            var second = crcBytes[1];
            receivedCrc = settings.MsByteFirst
                ? (ushort)((first << 8) | second)
                : (ushort)(first | (second << 8));

            var checkedBytes = new byte[payload.Length + settings.CheckCodeSize];
            payload.CopyTo(checkedBytes, 0);
            crcBytes.CopyTo(checkedBytes, payload.Length);
            calculatedResidue = new Crc(settings.CreateCrcOptions()!).Update(checkedBytes);
        }

        return new ReceivedData(
            frame,
            payload,
            message.CheckValid,
            true,
            receivedCrc,
            calculatedResidue,
            settings.ExpectedResidue,
            crcBytes);
    }
}

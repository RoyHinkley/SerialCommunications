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
        if (settings.CheckKind == MessageCheckKind.None)
            return new ReceivedData(frame, payload, null, false);

        if (settings.CheckKind != MessageCheckKind.Crc16)
            return new ReceivedData(frame, payload, message.CheckValid, false);

        ushort? receivedCrc = null;
        if (frame.Length >= 2)
        {
            var first = frame[^2];
            var second = frame[^1];
            receivedCrc = settings.MsByteFirst
                ? (ushort)((first << 8) | second)
                : (ushort)(first | (second << 8));
        }

        var crc = new Crc(settings.CreateCrcOptions()!);
        var calculatedResidue = crc.Update(frame);
        return new ReceivedData(
            frame,
            payload,
            message.CheckValid,
            true,
            receivedCrc,
            calculatedResidue,
            settings.ExpectedResidue);
    }
}

namespace SerialCommunications.Serial.Protocol;

/// <summary>Composes independent framing and check strategies into a message decoder.</summary>
internal sealed class MessageDecoder(IMessageFramer framer, IMessageCheck check) : IMessageDecoder
{
    private readonly IMessageFramer framer = framer ?? throw new ArgumentNullException(nameof(framer));
    private readonly IMessageCheck check = check ?? throw new ArgumentNullException(nameof(check));

    /// <inheritdoc />
    public bool HasPendingData => framer.HasPendingData;

    /// <inheritdoc />
    public void Consume(ReadOnlySpan<byte> bytes, Action<DecodedMessage> messageReady)
    {
        ArgumentNullException.ThrowIfNull(messageReady);
        framer.Consume(bytes, frame => Decode(frame, messageReady));
    }

    /// <inheritdoc />
    public void SilenceElapsed(Action<DecodedMessage> messageReady)
    {
        ArgumentNullException.ThrowIfNull(messageReady);
        framer.SilenceElapsed(frame => Decode(frame, messageReady));
    }

    /// <inheritdoc />
    public void Reset() => framer.Reset();

    private void Decode(ReadOnlyMemory<byte> frame, Action<DecodedMessage> messageReady)
    {
        var wireFrame = frame.ToArray();
        var valid = check.TryValidate(wireFrame, out var payload);
        messageReady(new DecodedMessage(wireFrame, payload, valid));
    }
}

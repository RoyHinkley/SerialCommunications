namespace SerialCommunications.Serial.Protocol;

/// <summary>Incrementally converts received byte chunks into framed, checked messages.</summary>
internal interface IMessageDecoder
{
    /// <summary>Whether the framing strategy currently holds bytes from an incomplete message.</summary>
    bool HasPendingData { get; }

    /// <summary>Consumes another transport-delivered chunk without assigning protocol meaning to the chunk boundary.</summary>
    void Consume(ReadOnlySpan<byte> bytes, Action<DecodedMessage> messageReady);

    /// <summary>Reports an explicit receive-silence event to framing strategies for which silence is meaningful.</summary>
    void SilenceElapsed(Action<DecodedMessage> messageReady);

    /// <summary>Discards incomplete receive state.</summary>
    void Reset();
}

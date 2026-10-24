namespace SerialCommunications.Serial.Protocol;

/// <summary>Composes serial byte acquisition, message decoding, and isolated consumer dispatch.</summary>
/// <remarks>
/// Transport notifications call <see cref="SignalDataAvailable"/> only. Acquisition and decoding run on the
/// receive-pump worker, while completed messages are delivered on the dispatch worker so application work cannot
/// delay continued serial reception.
/// </remarks>
internal sealed class SerialMessagePipeline : IDisposable
{
    private readonly MessageDispatchQueue dispatch;
    private readonly SerialReceiveAcquirer acquirer;
    private readonly ReceivePump receivePump;
    private bool disposed;

    public SerialMessagePipeline(
        ISerialTransport transport,
        IMessageDecoder decoder,
        Action<DecodedMessage> messageReceived,
        TimeSpan silenceInterval,
        Action<Exception>? receiveError = null,
        Action<Exception>? consumerError = null,
        Action<ReadOnlyMemory<byte>>? bytesAcquired = null,
        Action<DecodedMessage>? messageDecoded = null,
        int acquisitionBufferSize = 4096)
        : this(transport, decoder, messageReceived, () => silenceInterval, receiveError, consumerError, bytesAcquired, messageDecoded, acquisitionBufferSize)
    {
    }

    public SerialMessagePipeline(
        ISerialTransport transport,
        IMessageDecoder decoder,
        Action<DecodedMessage> messageReceived,
        Func<TimeSpan> silenceInterval,
        Action<Exception>? receiveError = null,
        Action<Exception>? consumerError = null,
        Action<ReadOnlyMemory<byte>>? bytesAcquired = null,
        Action<DecodedMessage>? messageDecoded = null,
        int acquisitionBufferSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(messageReceived);
        ArgumentNullException.ThrowIfNull(silenceInterval);

        dispatch = new MessageDispatchQueue(decoder, messageReceived, consumerError, messageDecoded);
        acquirer = new SerialReceiveAcquirer(
            transport,
            bytes =>
            {
                bytesAcquired?.Invoke(bytes);
                dispatch.Consume(bytes.Span);
            },
            acquisitionBufferSize);
        receivePump = new ReceivePump(
            () => acquirer.DrainAvailable(),
            dispatch.SilenceElapsed,
            silenceInterval,
            receiveError);
    }

    /// <summary>Signals transport receive activity without performing receive work on the notification path.</summary>
    public void SignalDataAvailable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        receivePump.SignalDataAvailable();
    }

    /// <summary>Stages a decoder for adoption immediately after the next completed message.</summary>
    public void StageDecoder(IMessageDecoder decoder, Action? applied = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        dispatch.StageDecoder(decoder, applied);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        dispatch.Reset();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        receivePump.Dispose();
        dispatch.Dispose();
    }
}

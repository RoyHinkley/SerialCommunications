namespace SerialCommunications.Serial;

/// <summary>Drains bytes already reported available by a serial transport and forwards them in blocks.</summary>
/// <remarks>
/// This class performs acquisition only. It assigns no message boundaries and performs no decoding, integrity
/// validation, logging, or application dispatch. The supplied consumer is invoked synchronously before the
/// reusable acquisition buffer may be overwritten.
/// </remarks>
internal sealed class SerialReceiveAcquirer
{
    private readonly ISerialTransport transport;
    private readonly Action<ReadOnlyMemory<byte>> bytesAcquired;
    private readonly byte[] buffer;

    public SerialReceiveAcquirer(
        ISerialTransport transport,
        Action<ReadOnlyMemory<byte>> bytesAcquired,
        int bufferSize = 4096)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.bytesAcquired = bytesAcquired ?? throw new ArgumentNullException(nameof(bytesAcquired));
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        buffer = new byte[bufferSize];
    }

    /// <summary>Drains all bytes currently reported available, reading no more than one buffer at a time.</summary>
    public int DrainAvailable()
    {
        var total = 0;
        while (true)
        {
            var available = transport.BytesToRead;
            if (available <= 0) return total;

            var requested = Math.Min(available, buffer.Length);
            var read = transport.Read(buffer, 0, requested);
            if (read <= 0) return total;
            if (read > requested)
                throw new InvalidOperationException($"Transport returned {read} bytes after a {requested}-byte read request.");

            total += read;
            bytesAcquired(buffer.AsMemory(0, read));
        }
    }
}

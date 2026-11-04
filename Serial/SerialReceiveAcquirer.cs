namespace SerialCommunications.Serial;

/// <summary>Drains bytes already reported available by a serial transport and forwards them in blocks.</summary>
/// <remarks>
/// This class performs acquisition only. It assigns no message boundaries and performs no decoding, integrity
/// validation, logging, or application dispatch. The supplied consumer is invoked synchronously before the
/// reusable acquisition buffer may be overwritten.
/// </remarks>
internal sealed class SerialReceiveAcquirer
{
    private readonly Func<int> bytesToRead;
    private readonly Func<byte[], int, int, int> read;
    private readonly Action<ReadOnlyMemory<byte>> bytesAcquired;
    private readonly byte[] buffer;

    public SerialReceiveAcquirer(
        ISerialTransport transport,
        Action<ReadOnlyMemory<byte>> bytesAcquired,
        int bufferSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(transport);
        bytesToRead = () => transport.BytesToRead;
        read = transport.Read;
        this.bytesAcquired = bytesAcquired ?? throw new ArgumentNullException(nameof(bytesAcquired));
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        buffer = new byte[bufferSize];
    }

    public SerialReceiveAcquirer(
        TransportSession session,
        Action<ReadOnlyMemory<byte>> bytesAcquired,
        int bufferSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.bytesAcquired = bytesAcquired ?? throw new ArgumentNullException(nameof(bytesAcquired));
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        buffer = new byte[bufferSize];
        bytesToRead = () =>
        {
            var status = session.GetBytesToRead(out var count);
            if (status != TransportSession.OperationResult.Succeeded)
                throw new IOException($"Receive availability query {status}: {session.LastFailure?.Message}");
            return count;
        };
        read = (data, offset, count) =>
        {
            var status = session.ReadBytes(data, offset, count, out var received);
            if (status != TransportSession.OperationResult.Succeeded)
                throw new IOException($"Receive operation {status}: {session.LastFailure?.Message}");
            return received;
        };
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
            var read = read(buffer, 0, requested);
            if (read <= 0) return total;
            if (read > requested)
                throw new InvalidOperationException($"Transport returned {read} bytes after a {requested}-byte read request.");

            total += read;
            bytesAcquired(buffer.AsMemory(0, read));
        }
    }
}

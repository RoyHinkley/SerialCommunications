namespace SerialCommunications.Serial;

/// <summary>Accumulated measurements for one <see cref="SerialChannel"/> lifetime.</summary>
/// <remarks>
/// Counters are monotonic until <see cref="Reset"/> is called. Connection and protocol state do
/// not belong here; this type contains measurements derived from operation only.
/// </remarks>
public sealed class SerialChannelTelemetry
{
    private long bytesReceived;
    private long messagesReceived;
    private long messagesTransmitted;
    private long checkFailures;
    private long receiveNotifications;
    private long recoveryAttempts;
    private long successfulRecoveries;

    /// <summary>Total bytes acquired from the serial transport.</summary>
    public long BytesReceived => Interlocked.Read(ref bytesReceived);

    /// <summary>Total messages decoded from the received byte stream, including invalid messages later suppressed from delivery.</summary>
    public long MessagesReceived => Interlocked.Read(ref messagesReceived);

    /// <summary>Total messages completely written to the serial transport.</summary>
    public long MessagesTransmitted => Interlocked.Read(ref messagesTransmitted);

    /// <summary>Total received messages that failed their configured check.</summary>
    public long CheckFailures => Interlocked.Read(ref checkFailures);

    /// <summary>Total transport notifications indicating that received data may be available.</summary>
    public long ReceiveNotifications => Interlocked.Read(ref receiveNotifications);

    /// <summary>Total automatic transport-recovery attempts made during this channel lifetime.</summary>
    public long RecoveryAttempts => Interlocked.Read(ref recoveryAttempts);

    /// <summary>Total automatic transport-recovery attempts that restored a connection.</summary>
    public long SuccessfulRecoveries => Interlocked.Read(ref successfulRecoveries);

    /// <summary>Clears accumulated measurements without affecting channel operation.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref bytesReceived, 0);
        Interlocked.Exchange(ref messagesReceived, 0);
        Interlocked.Exchange(ref messagesTransmitted, 0);
        Interlocked.Exchange(ref checkFailures, 0);
        Interlocked.Exchange(ref receiveNotifications, 0);
        Interlocked.Exchange(ref recoveryAttempts, 0);
        Interlocked.Exchange(ref successfulRecoveries, 0);
    }

    internal void RecordBytesReceived(int count) => Interlocked.Add(ref bytesReceived, count);
    internal void RecordMessageReceived() => Interlocked.Increment(ref messagesReceived);
    internal void RecordMessageTransmitted() => Interlocked.Increment(ref messagesTransmitted);
    internal void RecordCheckFailure() => Interlocked.Increment(ref checkFailures);
    internal void RecordReceiveNotification() => Interlocked.Increment(ref receiveNotifications);
    internal void RecordRecoveryAttempt() => Interlocked.Increment(ref recoveryAttempts);
    internal void RecordSuccessfulRecovery() => Interlocked.Increment(ref successfulRecoveries);
}

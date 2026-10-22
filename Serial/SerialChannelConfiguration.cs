namespace SerialCommunications.Serial;

/// <summary>
/// Reusable configuration for the communications behavior of a <see cref="SerialChannel"/>.
/// </summary>
/// <remarks>
/// This is deliberately independent of persistence, UI representation, named profiles, and
/// application defaults. Consumers such as SerialBench and AeonHacs may construct it in their
/// own native configuration systems.
/// </remarks>
/// <param name="PortSettings">Serial-port settings used whenever the channel acquires a transport.</param>
public sealed record SerialChannelConfiguration(SerialPortSettings PortSettings)
{
    /// <summary>Controls the RTS output when the transport is opened.</summary>
    public SerialChannel.RtsModes RtsMode { get; init; } = SerialChannel.RtsModes.Enabled;

    /// <summary>
    /// Minimum delay in milliseconds between logical messages when writing a complete message at once;
    /// -1 disables message-level pacing.
    /// </summary>
    public int MillisecondsBetweenMessages { get; init; } = -1;

    /// <summary>
    /// Minimum delay in milliseconds between individual transmitted bytes; -1 writes each logical message
    /// in one transport operation instead of byte pacing.
    /// </summary>
    public int MillisecondsBetweenBytes { get; init; } = -1;

    /// <summary>
    /// Receive silence interval, in milliseconds, used to complete silence-framed messages. Values below
    /// one millisecond are treated as one millisecond when a receive session is created.
    /// </summary>
    public int MaximumMillisecondsSilenceInMessage { get; init; } = 5;

    /// <summary>Message framing, check calculation, and invalid-message delivery policy.</summary>
    public SerialProtocolSettings ProtocolSettings { get; init; } = SerialProtocolSettings.From(null, false);

    /// <summary>Policy governing automatic reacquisition after a requested transport becomes unavailable.</summary>
    public SerialRecoveryPolicy RecoveryPolicy { get; init; } = SerialRecoveryPolicy.Default;
}

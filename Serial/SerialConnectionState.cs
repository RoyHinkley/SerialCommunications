namespace SerialCommunications.Serial;

/// <summary>Describes the availability of a requested serial transport.</summary>
/// <remarks>
/// Connection intent, transport availability, and communications health are deliberately separate concepts.
/// In particular, <see cref="Unavailable"/> does not mean that the user has disconnected: the requested
/// connection may remain active while the channel owns no OS serial-port handle and periodically attempts
/// to reacquire the configured endpoint.
/// </remarks>
public enum SerialConnectionState
{
    /// <summary>No connection is requested and no serial transport is owned.</summary>
    Disconnected,

    /// <summary>The channel is attempting to acquire and open the configured serial transport.</summary>
    Connecting,

    /// <summary>The configured serial transport is open and owned by the channel.</summary>
    Connected,

    /// <summary>The channel is intentionally stopping workers and releasing its serial transport.</summary>
    Disconnecting,

    /// <summary>
    /// A connection is still requested but the configured transport is unavailable; automatic recovery may
    /// continue according to the active <see cref="SerialRecoveryPolicy"/>.
    /// </summary>
    Unavailable,

    /// <summary>The channel has been disposed and cannot be connected or used again.</summary>
    Disposed
}

namespace SerialCommunications.Serial.Protocol;

/// <summary>Check strategy for protocols that carry payload bytes without a check field.</summary>
public sealed class NoMessageCheck : IMessageCheck
{
    /// <summary>The shared stateless instance.</summary>
    public static NoMessageCheck Instance { get; } = new();

    private NoMessageCheck() { }

    /// <summary>No check bytes are appended.</summary>
    public int FieldLength => 0;

    /// <inheritdoc />
    public byte[] Append(ReadOnlySpan<byte> payload) => payload.ToArray();

    /// <inheritdoc />
    public bool TryValidate(ReadOnlySpan<byte> frame, out byte[] payload)
    {
        payload = frame.ToArray();
        return true;
    }
}

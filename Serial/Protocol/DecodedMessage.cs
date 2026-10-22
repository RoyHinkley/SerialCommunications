namespace SerialCommunications.Serial.Protocol;

/// <summary>One framed wire message together with its check result.</summary>
/// <remarks>
/// <see cref="Frame"/> contains the bytes emitted by the framer after framing bytes have been removed.
/// <see cref="Payload"/> contains the application bytes with the check field removed. A failed check remains
/// a decoded message so diagnostics can preserve the exact received frame and candidate payload.
/// </remarks>
internal sealed record DecodedMessage(ReadOnlyMemory<byte> Frame, ReadOnlyMemory<byte> Payload, bool CheckValid);

namespace SerialCommunications.Serial.Protocol;

/// <summary>One framed wire message together with its check result.</summary>
/// <remarks>
/// <see cref="Frame"/> contains the complete received frame, including configured S, E, and C elements.
/// <see cref="Payload"/> contains only P. A failed check remains a decoded message so diagnostics can preserve
/// the exact received frame and candidate payload.
/// </remarks>
internal sealed record DecodedMessage(ReadOnlyMemory<byte> Frame, ReadOnlyMemory<byte> Payload, bool CheckValid);

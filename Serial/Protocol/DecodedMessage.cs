namespace SerialCommunications.Serial.Protocol;

/// <summary>One recognized wire message together with its check result.</summary>
/// <remarks>
/// <see cref="Frame"/> preserves every byte received for the recognized message, including bytes preceding S and
/// the configured S, E, and C elements. <see cref="Payload"/> contains only P. Framing and check elements are
/// excluded from the payload, not discarded from the diagnostic frame. A failed check remains a decoded message
/// so diagnostics can preserve the exact received bytes and candidate payload.
/// </remarks>
internal sealed record DecodedMessage(ReadOnlyMemory<byte> Frame, ReadOnlyMemory<byte> Payload, bool CheckValid);

namespace SerialCommunications.Serial.Protocol;

/// <summary>One recognized wire message together with its framing and check results.</summary>
/// <remarks>
/// <see cref="Frame"/> preserves every byte received for the recognized message, including bytes preceding S and
/// the configured S, E, and C elements. <see cref="Payload"/> contains only P when framing is complete. For an
/// incomplete frame resolved by receive silence, it contains the best unambiguous payload candidate and
/// <see cref="FramingValid"/> is false. A failed framing or check result remains a decoded message so diagnostics
/// can preserve the exact received bytes.
/// </remarks>
internal sealed record DecodedMessage(
    ReadOnlyMemory<byte> Frame,
    ReadOnlyMemory<byte> Payload,
    bool FramingValid,
    bool CheckValid);

namespace SerialCommunications.Serial.Protocol;

/// <summary>
/// Adds and validates the check field associated with one protocol frame.
/// </summary>
/// <remarks>
/// Framing and checking are deliberately separate concepts. A check strategy receives a frame whose
/// framing bytes have already been removed, and returns the payload after removing its check field.
/// Protocols in which check bytes participate in boundary recognition may provide a specialized framer
/// that collaborates with the same strategy.
/// </remarks>
public interface IMessageCheck
{
    /// <summary>Number of check bytes appended to a payload.</summary>
    int FieldLength { get; }

    /// <summary>Appends this strategy's check field to exact payload bytes.</summary>
    byte[] Append(ReadOnlySpan<byte> payload);

    /// <summary>
    /// Checks a payload plus check field and returns the payload bytes independently of the result.
    /// </summary>
    /// <remarks>
    /// When the frame is long enough to contain the check field, <paramref name="payload"/> is the frame
    /// with that field removed even when the check fails. This preserves the received application bytes for
    /// diagnostics; a check failure is evidence about those bytes, not a reason to erase them. A frame too
    /// short to contain the check field returns an empty payload.
    /// </remarks>
    bool TryValidate(ReadOnlySpan<byte> frame, out byte[] payload);
}

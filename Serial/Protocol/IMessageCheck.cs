namespace SerialCommunications.Serial.Protocol;

/// <summary>Adds and validates the check field associated with one protocol frame.</summary>
/// <remarks>
/// A check strategy operates on P+C only: framing elements S and E are excluded before validation. Frame layout
/// remains the authority for whether C is present and where it occurs on the wire. The receive decoder may use
/// validation while recognizing an otherwise ambiguous boundary, as required by the exceptional PCE layout.
/// </remarks>
public interface IMessageCheck
{
    /// <summary>Number of check bytes appended to a payload.</summary>
    int FieldLength { get; }

    /// <summary>Appends this strategy's check field to exact payload bytes.</summary>
    byte[] Append(ReadOnlySpan<byte> payload);

    /// <summary>Checks P+C and returns P independently of whether the check succeeds.</summary>
    /// <remarks>
    /// When the frame is long enough to contain the check field, <paramref name="payload"/> is the frame
    /// with that field removed even when the check fails. This preserves the received application bytes for
    /// diagnostics; a check failure is evidence about those bytes, not a reason to erase them. A frame too
    /// short to contain the check field returns an empty payload.
    /// </remarks>
    bool TryValidate(ReadOnlySpan<byte> frame, out byte[] payload);
}

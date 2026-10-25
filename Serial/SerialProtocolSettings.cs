using SerialCommunications.Serial.Protocol;

namespace SerialCommunications.Serial;

/// <summary>Identifies the reusable calculation applied to a message check field.</summary>
public enum MessageCheckKind
{
    /// <summary>No message check field is present.</summary>
    None,
    /// <summary>A configurable 16-bit cyclic redundancy check is appended and validated.</summary>
    Crc16
}

/// <summary>
/// Specifies the ordered elements of a serial message frame, where S is the start element,
/// P is the payload, E is the end element, and C is the message check field.
/// </summary>
public enum FrameLayout
{
    /// <summary>Payload.</summary>
    P,
    /// <summary>Payload followed by end.</summary>
    PE,
    /// <summary>Payload followed by check.</summary>
    PC,
    /// <summary>Payload followed by end, then check.</summary>
    PEC,
    /// <summary>Start followed by payload.</summary>
    SP,
    /// <summary>Start followed by payload, then end.</summary>
    SPE,
    /// <summary>Start followed by payload, then check.</summary>
    SPC,
    /// <summary>Start followed by payload, end, then check.</summary>
    SPEC,
    /// <summary>Payload followed by check, then end.</summary>
    PCE
}

/// <summary>Immutable snapshot of settings that determine serial message framing and checking behavior.</summary>
/// <remarks>
/// A snapshot is deliberately value-based: UI/configuration edits must never mutate protocol state that a
/// transmit or receive operation is already using. A new snapshot can therefore be staged and adopted only
/// at a known message boundary. Check calculations operate on the supplied binary payload; protocol-specific
/// byte selection, bypass rules, field names, and textual representations belong above this layer.
/// CRC-specific fields are meaningful only when <see cref="CheckKind"/> is <see cref="MessageCheckKind.Crc16"/>.
/// </remarks>
/// <param name="Layout">Ordered framing elements present on the wire.</param>
/// <param name="Start">Literal byte sequence represented by S in <paramref name="Layout"/>; empty when S is absent.</param>
/// <param name="End">Literal byte sequence represented by E in <paramref name="Layout"/>; empty when E is absent.</param>
public sealed record SerialProtocolSettings(
    FrameLayout Layout,
    byte[] Start,
    byte[] End,
    MessageCheckKind CheckKind,
    ushort InitialValue,
    ushort Polynomial,
    ushort ExpectedResidue,
    bool PostInvert,
    bool MsBitFirst,
    bool MsByteFirst,
    byte TermChar,
    bool OmitTermChar,
    bool SuppressInvalidMessages)
{
    /// <summary>Number of bytes occupied by C in the configured frame layout; zero when no check field is present.</summary>
    public int CheckCodeSize => CreateMessageCheck().FieldLength;

    /// <summary>Creates settings with no message check field.</summary>
    public static SerialProtocolSettings None(
        FrameLayout layout = FrameLayout.P,
        byte[]? start = null,
        byte[]? end = null,
        bool suppressInvalidMessages = false) =>
        new(layout, Snapshot(start), Snapshot(end), MessageCheckKind.None, 0, 0, 0, false, false, false, 0, true,
            suppressInvalidMessages);

    /// <summary>Creates settings for a configurable 16-bit CRC.</summary>
    public static SerialProtocolSettings Crc16(
        CrcOptions options,
        FrameLayout? layout = null,
        byte[]? start = null,
        byte[]? end = null,
        bool suppressInvalidMessages = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        var resolvedLayout = layout ?? (options.OmitTermChar ? FrameLayout.PC : FrameLayout.PCE);
        var resolvedEnd = end is not null
            ? Snapshot(end)
            : options.OmitTermChar ? [] : [options.TermChar];
        return new(resolvedLayout, Snapshot(start), resolvedEnd, MessageCheckKind.Crc16, options.InitialValue,
            options.Polynomial, options.ExpectedResidue, options.PostInvert, options.MsBitFirst, options.MsByteFirst,
            options.TermChar, options.OmitTermChar, suppressInvalidMessages);
    }

    /// <summary>Creates a protocol snapshot from CRC options and invalid-message delivery policy.</summary>
    public static SerialProtocolSettings From(CrcOptions? crc, bool suppressInvalidMessages) =>
        crc is null ? None(suppressInvalidMessages: suppressInvalidMessages) : Crc16(crc, suppressInvalidMessages: suppressInvalidMessages);

    internal CrcOptions? CreateCrcOptions() => CheckKind != MessageCheckKind.Crc16 ? null : new CrcOptions(
        InitialValue, Polynomial, ExpectedResidue, PostInvert, MsBitFirst, MsByteFirst, TermChar, OmitTermChar);

    /// <summary>Creates the configured reusable message-check calculation.</summary>
    public IMessageCheck CreateMessageCheck() => CheckKind switch
    {
        MessageCheckKind.None => NoMessageCheck.Instance,
        MessageCheckKind.Crc16 => new Crc16MessageCheck(CreateCrcOptions()!),
        _ => throw new InvalidOperationException($"Unsupported message check kind: {CheckKind}.")
    };

    internal bool UsesSameFramingStrategyAs(SerialProtocolSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return CheckKind == other.CheckKind && OmitTermChar == other.OmitTermChar;
    }

    internal IMessageDecoder CreateMessageDecoder()
    {
        var check = CreateMessageCheck();
        IMessageFramer framer = CheckKind != MessageCheckKind.None && !OmitTermChar
            ? new TrailerAwareTerminatorMessageFramer(TermChar, check)
            : new SilenceMessageFramer();

        return new MessageDecoder(framer, check);
    }

    private static byte[] Snapshot(byte[]? bytes) => bytes is null || bytes.Length == 0 ? [] : [.. bytes];
}

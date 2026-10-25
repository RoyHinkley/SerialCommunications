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
    P, PE, PC, PEC, SP, SPE, SPC, SPEC, PCE
}

/// <summary>Immutable snapshot of settings that determine serial message framing and checking behavior.</summary>
/// <remarks>
/// S and E are literal byte sequences and are defensively copied. Check calculations apply to P only;
/// S and E never participate in the check. The exceptional PCE layout uses check validation while receiving
/// so an E byte sequence occurring inside C is not mistaken for the end of the frame.
/// </remarks>
public sealed record SerialProtocolSettings
{
    private readonly byte[] start;
    private readonly byte[] end;

    public FrameLayout Layout { get; init; }
    /// <summary>Literal byte sequence represented by S; empty when S is absent.</summary>
    public byte[] Start => [.. start];
    /// <summary>Literal byte sequence represented by E; empty when E is absent.</summary>
    public byte[] End => [.. end];
    public MessageCheckKind CheckKind { get; init; }
    public ushort InitialValue { get; init; }
    public ushort Polynomial { get; init; }
    public ushort ExpectedResidue { get; init; }
    public bool PostInvert { get; init; }
    public bool MsBitFirst { get; init; }
    public bool MsByteFirst { get; init; }
    public bool SuppressInvalidMessages { get; init; }

    private SerialProtocolSettings(FrameLayout layout, byte[]? start, byte[]? end, MessageCheckKind checkKind,
        ushort initialValue, ushort polynomial, ushort expectedResidue, bool postInvert, bool msBitFirst,
        bool msByteFirst, bool suppressInvalidMessages)
    {
        Layout = layout;
        this.start = Snapshot(start);
        this.end = Snapshot(end);
        CheckKind = checkKind;
        InitialValue = initialValue;
        Polynomial = polynomial;
        ExpectedResidue = expectedResidue;
        PostInvert = postInvert;
        MsBitFirst = msBitFirst;
        MsByteFirst = msByteFirst;
        SuppressInvalidMessages = suppressInvalidMessages;
        Validate();
    }

    /// <summary>Number of bytes occupied by C in the configured frame layout; zero when no check field is present.</summary>
    public int CheckCodeSize => CheckKind switch { MessageCheckKind.None => 0, MessageCheckKind.Crc16 => 2, _ => throw new InvalidOperationException() };

    public static SerialProtocolSettings None(FrameLayout layout = FrameLayout.P, byte[]? start = null, byte[]? end = null,
        bool suppressInvalidMessages = false) =>
        new(layout, start, end, MessageCheckKind.None, 0, 0, 0, false, false, false, suppressInvalidMessages);

    public static SerialProtocolSettings None(FrameLayout layout, byte start, byte[]? end = null,
        bool suppressInvalidMessages = false) => None(layout, [start], end, suppressInvalidMessages);
    public static SerialProtocolSettings None(FrameLayout layout, byte[]? start, byte end,
        bool suppressInvalidMessages = false) => None(layout, start, [end], suppressInvalidMessages);
    public static SerialProtocolSettings None(FrameLayout layout, byte start, byte end,
        bool suppressInvalidMessages = false) => None(layout, [start], [end], suppressInvalidMessages);

    /// <summary>Creates CRC settings. When layout is omitted, legacy CrcOptions framing is translated to PC or PCE.</summary>
    public static SerialProtocolSettings Crc16(CrcOptions options, FrameLayout? layout = null, byte[]? start = null,
        byte[]? end = null, bool suppressInvalidMessages = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        var resolvedLayout = layout ?? (options.OmitTermChar ? FrameLayout.PC : FrameLayout.PCE);
        var resolvedEnd = end is not null ? end : options.OmitTermChar ? [] : [options.TermChar];
        return new(resolvedLayout, start, resolvedEnd, MessageCheckKind.Crc16, options.InitialValue, options.Polynomial,
            options.ExpectedResidue, options.PostInvert, options.MsBitFirst, options.MsByteFirst, suppressInvalidMessages);
    }

    public static SerialProtocolSettings Crc16(CrcOptions options, FrameLayout layout, byte start, byte[]? end = null,
        bool suppressInvalidMessages = false) => Crc16(options, layout, [start], end, suppressInvalidMessages);
    public static SerialProtocolSettings Crc16(CrcOptions options, FrameLayout layout, byte[]? start, byte end,
        bool suppressInvalidMessages = false) => Crc16(options, layout, start, [end], suppressInvalidMessages);
    public static SerialProtocolSettings Crc16(CrcOptions options, FrameLayout layout, byte start, byte end,
        bool suppressInvalidMessages = false) => Crc16(options, layout, [start], [end], suppressInvalidMessages);

    public static SerialProtocolSettings From(CrcOptions? crc, bool suppressInvalidMessages) =>
        crc is null ? None(suppressInvalidMessages: suppressInvalidMessages) : Crc16(crc, suppressInvalidMessages: suppressInvalidMessages);

    internal CrcOptions? CreateCrcOptions() => CheckKind != MessageCheckKind.Crc16 ? null :
        new CrcOptions(InitialValue, Polynomial, ExpectedResidue, PostInvert, MsBitFirst, MsByteFirst, 0, true);

    public IMessageCheck CreateMessageCheck() => CheckKind switch
    {
        MessageCheckKind.None => NoMessageCheck.Instance,
        MessageCheckKind.Crc16 => new Crc16MessageCheck(CreateCrcOptions()!),
        _ => throw new InvalidOperationException($"Unsupported message check kind: {CheckKind}.")
    };

    /// <summary>Builds the exact wire frame for payload P.</summary>
    public byte[] EncodeFrame(ReadOnlySpan<byte> payload)
    {
        Validate();
        var p = payload.ToArray();
        var checkedPayload = LayoutHasCheck(Layout) ? CreateMessageCheck().Append(payload) : p;
        var c = LayoutHasCheck(Layout) ? checkedPayload.AsSpan(p.Length).ToArray() : [];
        return Layout switch
        {
            FrameLayout.P => p,
            FrameLayout.PE => [.. p, .. end],
            FrameLayout.PC => checkedPayload,
            FrameLayout.PEC => [.. p, .. end, .. c],
            FrameLayout.SP => [.. start, .. p],
            FrameLayout.SPE => [.. start, .. p, .. end],
            FrameLayout.SPC => [.. start, .. checkedPayload],
            FrameLayout.SPEC => [.. start, .. p, .. end, .. c],
            FrameLayout.PCE => [.. checkedPayload, .. end],
            _ => throw new InvalidOperationException($"Unsupported frame layout: {Layout}.")
        };
    }

    internal bool UsesSameFramingStrategyAs(SerialProtocolSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Layout != other.Layout || !start.AsSpan().SequenceEqual(other.start) || !end.AsSpan().SequenceEqual(other.end) ||
            CheckCodeSize != other.CheckCodeSize) return false;
        return Layout != FrameLayout.PCE || SameCheckAs(other);
    }

    internal IMessageDecoder CreateMessageDecoder() { Validate(); return new LayoutAwareMessageDecoder(this); }

    private bool SameCheckAs(SerialProtocolSettings other) => CheckKind == other.CheckKind &&
        InitialValue == other.InitialValue && Polynomial == other.Polynomial && ExpectedResidue == other.ExpectedResidue &&
        PostInvert == other.PostInvert && MsBitFirst == other.MsBitFirst && MsByteFirst == other.MsByteFirst;

    private void Validate()
    {
        var hasS = Layout is FrameLayout.SP or FrameLayout.SPE or FrameLayout.SPC or FrameLayout.SPEC;
        var hasE = Layout is FrameLayout.PE or FrameLayout.PEC or FrameLayout.SPE or FrameLayout.SPEC or FrameLayout.PCE;
        var hasC = LayoutHasCheck(Layout);
        if (hasS != (start.Length != 0)) throw new ArgumentException($"Layout {Layout} {(hasS ? "requires" : "does not contain")} S.");
        if (hasE != (end.Length != 0)) throw new ArgumentException($"Layout {Layout} {(hasE ? "requires" : "does not contain")} E.");
        if (hasC != (CheckKind != MessageCheckKind.None)) throw new ArgumentException($"Layout {Layout} {(hasC ? "requires" : "does not contain")} C.");
    }

    private static bool LayoutHasCheck(FrameLayout layout) => layout is FrameLayout.PC or FrameLayout.PEC or FrameLayout.SPC or FrameLayout.SPEC or FrameLayout.PCE;
    private static byte[] Snapshot(byte[]? bytes) => bytes is null || bytes.Length == 0 ? [] : [.. bytes];
}

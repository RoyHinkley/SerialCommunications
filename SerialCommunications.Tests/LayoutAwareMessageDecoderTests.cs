using SerialCommunications.Serial;
using SerialCommunications.Serial.Protocol;

namespace SerialCommunications.Tests;

public sealed class LayoutAwareMessageDecoderTests
{
    private static CrcOptions CrcOptions() => new()
    {
        Polynomial = 0xA001,
        InitialValue = 0xFFFF,
        ExpectedResidue = 0x0000,
        PostInvert = false,
        MsBitFirst = false,
        MsByteFirst = false,
        TermChar = 0x03,
        OmitTermChar = true
    };

    public static TheoryData<FrameLayout> Layouts => new()
    {
        FrameLayout.P, FrameLayout.PE, FrameLayout.PC, FrameLayout.PEC,
        FrameLayout.SP, FrameLayout.SPE, FrameLayout.SPC, FrameLayout.SPEC, FrameLayout.PCE
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void All_layouts_preserve_full_frame_and_extract_only_payload(FrameLayout layout)
    {
        byte[] start = [0xAA, 0x55];
        byte[] end = [0x0D, 0x0A];
        byte[] payload = [0x41, 0x42, 0x43];
        var hasCheck = layout is FrameLayout.PC or FrameLayout.PEC or FrameLayout.SPC or FrameLayout.SPEC or FrameLayout.PCE;
        var settings = hasCheck
            ? SerialProtocolSettings.Crc16(CrcOptions(), layout, start, end)
            : SerialProtocolSettings.None(layout, start, end);
        var wire = BuildWire(settings, payload);
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();

        foreach (var value in wire) decoder.Consume([value], messages.Add);
        decoder.SilenceElapsed(messages.Add);

        var message = Assert.Single(messages);
        Assert.Equal(wire, message.Frame.ToArray());
        Assert.Equal(payload, message.Payload.ToArray());
        Assert.True(message.CheckValid);
    }

    [Fact]
    public void Start_layout_retains_bytes_preceding_start_but_excludes_them_from_payload()
    {
        byte[] start = [0xAA, 0x55];
        byte[] end = [0x0D, 0x0A];
        var settings = SerialProtocolSettings.None(FrameLayout.SPE, start, end);
        byte[] wire = [0x99, 0x98, .. start, 0x41, 0x42, .. end];
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();

        decoder.Consume(wire, messages.Add);

        var message = Assert.Single(messages);
        Assert.Equal(wire, message.Frame.ToArray());
        Assert.Equal(new byte[] { 0x41, 0x42 }, message.Payload.ToArray());
    }

    [Fact]
    public void Pce_does_not_mistake_end_value_inside_crc_for_frame_boundary()
    {
        var options = CrcOptions();
        byte[] end = [0x03];
        var settings = SerialProtocolSettings.Crc16(options, FrameLayout.PCE, end: end);
        var check = settings.CreateMessageCheck();
        var payload = FindPayload(check, end[0]);
        var wire = check.Append(payload).Concat(end).ToArray();
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();

        foreach (var value in wire) decoder.Consume([value], messages.Add);

        var message = Assert.Single(messages);
        Assert.Equal(wire, message.Frame.ToArray());
        Assert.Equal(payload, message.Payload.ToArray());
        Assert.True(message.CheckValid);
    }

    private static byte[] BuildWire(SerialProtocolSettings settings, byte[] payload)
    {
        var checkedPayload = settings.CreateMessageCheck().Append(payload);
        return settings.Layout switch
        {
            FrameLayout.P => payload,
            FrameLayout.PE => [.. payload, .. settings.End],
            FrameLayout.PC => checkedPayload,
            FrameLayout.PEC => [.. payload, .. settings.End, .. checkedPayload[^settings.CheckCodeSize..]],
            FrameLayout.SP => [.. settings.Start, .. payload],
            FrameLayout.SPE => [.. settings.Start, .. payload, .. settings.End],
            FrameLayout.SPC => [.. settings.Start, .. checkedPayload],
            FrameLayout.SPEC => [.. settings.Start, .. payload, .. settings.End, .. checkedPayload[^settings.CheckCodeSize..]],
            FrameLayout.PCE => [.. checkedPayload, .. settings.End],
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private static byte[] FindPayload(IMessageCheck check, byte end)
    {
        for (var value = 0; value <= ushort.MaxValue; value++)
        {
            byte[] payload = [(byte)value, (byte)(value >> 8)];
            if (payload.Contains(end)) continue;
            var checkedPayload = check.Append(payload);
            if (checkedPayload[^2] == end || checkedPayload[^1] == end) return payload;
        }
        throw new InvalidOperationException("Unable to synthesize CRC/end ambiguity.");
    }
}

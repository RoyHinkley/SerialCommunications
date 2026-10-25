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
        MsByteFirst = false
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
        var wire = settings.EncodeFrame(payload);
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

    [Theory]
    [InlineData(FrameLayout.PE)]
    [InlineData(FrameLayout.SPE)]
    public void Empty_end_uses_silence_boundary(FrameLayout layout)
    {
        byte[] payload = [0x41, 0x42];
        var settings = SerialProtocolSettings.None(layout);
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();

        decoder.Consume(payload, messages.Add);
        Assert.Empty(messages);
        decoder.SilenceElapsed(messages.Add);

        var message = Assert.Single(messages);
        Assert.Equal(payload, message.Payload.ToArray());
    }

    [Theory]
    [InlineData(FrameLayout.PEC)]
    [InlineData(FrameLayout.SPEC)]
    [InlineData(FrameLayout.PCE)]
    public void Empty_end_with_check_uses_silence_and_still_extracts_check(FrameLayout layout)
    {
        byte[] payload = [0x41, 0x42];
        var settings = SerialProtocolSettings.Crc16(CrcOptions(), layout);
        var wire = settings.EncodeFrame(payload);
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();

        decoder.Consume(wire, messages.Add);
        Assert.Empty(messages);
        decoder.SilenceElapsed(messages.Add);

        var message = Assert.Single(messages);
        Assert.Equal(wire, message.Frame.ToArray());
        Assert.Equal(payload, message.Payload.ToArray());
        Assert.True(message.CheckValid);
    }

    [Fact]
    public void Configured_check_is_dormant_when_layout_has_no_check_code()
    {
        byte[] payload = [0x41, 0x42];
        var settings = SerialProtocolSettings.Crc16(CrcOptions(), FrameLayout.P);
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();

        decoder.Consume(payload, messages.Add);
        decoder.SilenceElapsed(messages.Add);

        var message = Assert.Single(messages);
        Assert.Equal(payload, message.Payload.ToArray());
        Assert.True(message.CheckValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Pce_does_not_mistake_end_value_in_either_crc_byte_for_frame_boundary(int crcIndex)
    {
        var options = CrcOptions();
        byte[] end = [0x03];
        var settings = SerialProtocolSettings.Crc16(options, FrameLayout.PCE, end: end);
        var check = settings.CreateMessageCheck();
        var payload = FindPayload(check, end[0], crcIndex);
        var checkedPayload = check.Append(payload);
        var wire = checkedPayload.Concat(end).ToArray();
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();
        var checkStart = checkedPayload.Length - check.FieldLength;

        Assert.Equal(end[0], checkedPayload[checkStart + crcIndex]);

        foreach (var value in wire) decoder.Consume([value], messages.Add);

        var message = Assert.Single(messages);
        Assert.Equal(wire, message.Frame.ToArray());
        Assert.Equal(payload, message.Payload.ToArray());
        Assert.True(message.CheckValid);
    }

    private static byte[] FindPayload(IMessageCheck check, byte end, int crcIndex)
    {
        for (var value = 0; value <= ushort.MaxValue; value++)
        {
            byte[] payload = [(byte)value, (byte)(value >> 8)];
            if (payload.Contains(end)) continue;
            var checkedPayload = check.Append(payload);
            var checkStart = checkedPayload.Length - check.FieldLength;
            if (checkedPayload[checkStart + crcIndex] == end) return payload;
        }
        throw new InvalidOperationException($"Unable to synthesize CRC/end ambiguity at CRC byte {crcIndex}.");
    }
}

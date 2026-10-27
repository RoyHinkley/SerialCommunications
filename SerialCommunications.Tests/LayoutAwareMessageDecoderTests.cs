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

    public static TheoryData<FrameLayout> ExplicitEndLayouts => new()
    {
        FrameLayout.PE, FrameLayout.PEC, FrameLayout.SPE, FrameLayout.SPEC, FrameLayout.PCE
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void All_layouts_preserve_full_frame_and_extract_only_payload(FrameLayout layout)
    {
        byte[] start = [0xAA, 0x55];
        byte[] end = [0x0D, 0x0A];
        byte[] payload = [0x41, 0x42, 0x43];
        var settings = Settings(layout, start, end);
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
    [MemberData(nameof(ExplicitEndLayouts))]
    public void Multi_byte_end_is_recognized_across_every_receive_split(FrameLayout layout)
    {
        byte[] start = [0xA5, 0x5A];
        byte[] end = [0x0D, 0x0A];
        byte[] payload = [0x41, 0x42, 0x43];
        var settings = Settings(layout, start, end);
        var wire = settings.EncodeFrame(payload);

        for (var split = 0; split <= wire.Length; split++)
        {
            var decoder = new LayoutAwareMessageDecoder(settings);
            var messages = new List<DecodedMessage>();
            decoder.Consume(wire.AsSpan(0, split), messages.Add);
            decoder.Consume(wire.AsSpan(split), messages.Add);

            var message = Assert.Single(messages);
            Assert.Equal(wire, message.Frame.ToArray());
            Assert.Equal(payload, message.Payload.ToArray());
            Assert.True(message.CheckValid);
        }
    }

    [Theory]
    [InlineData(FrameLayout.PE)]
    [InlineData(FrameLayout.PEC)]
    [InlineData(FrameLayout.SPE)]
    [InlineData(FrameLayout.SPEC)]
    [InlineData(FrameLayout.PCE)]
    public void Back_to_back_explicit_end_frames_are_emitted_independently(FrameLayout layout)
    {
        byte[] start = [0xA5, 0x5A];
        byte[] end = [0x0D, 0x0A];
        byte[] firstPayload = [0x31, 0x32];
        byte[] secondPayload = [0x41, 0x42, 0x43];
        var settings = Settings(layout, start, end);
        var firstWire = settings.EncodeFrame(firstPayload);
        var secondWire = settings.EncodeFrame(secondPayload);
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();

        decoder.Consume([.. firstWire, .. secondWire], messages.Add);

        Assert.Equal(2, messages.Count);
        Assert.Equal(firstWire, messages[0].Frame.ToArray());
        Assert.Equal(firstPayload, messages[0].Payload.ToArray());
        Assert.Equal(secondWire, messages[1].Frame.ToArray());
        Assert.Equal(secondPayload, messages[1].Payload.ToArray());
        Assert.All(messages, message => Assert.True(message.CheckValid));
    }

    [Fact]
    public void Partial_multi_byte_end_prefix_does_not_complete_frame()
    {
        byte[] end = [0x0D, 0x0A];
        var settings = SerialProtocolSettings.None(FrameLayout.PE, end: end);
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();

        decoder.Consume([0x41, 0x0D], messages.Add);
        Assert.Empty(messages);
        Assert.True(decoder.HasPendingData);

        decoder.Consume([0x0A], messages.Add);
        var message = Assert.Single(messages);
        Assert.Equal(new byte[] { 0x41 }, message.Payload.ToArray());
    }

    [Theory]
    [InlineData(FrameLayout.PEC)]
    [InlineData(FrameLayout.SPEC)]
    public void Check_after_end_can_span_receive_chunks(FrameLayout layout)
    {
        byte[] start = [0xA5];
        byte[] end = [0x0D, 0x0A];
        byte[] payload = [0x41, 0x42];
        var settings = SerialProtocolSettings.Crc16(CrcOptions(), layout, start, end);
        var wire = settings.EncodeFrame(payload);
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();

        decoder.Consume(wire.AsSpan(0, wire.Length - 1), messages.Add);
        Assert.Empty(messages);
        Assert.True(decoder.HasPendingData);

        decoder.Consume(wire.AsSpan(wire.Length - 1), messages.Add);
        var message = Assert.Single(messages);
        Assert.Equal(wire, message.Frame.ToArray());
        Assert.True(message.FramingValid);
        Assert.True(message.CheckValid);
    }

    [Theory]
    [InlineData(FrameLayout.PC)]
    [InlineData(FrameLayout.PEC)]
    [InlineData(FrameLayout.SPC)]
    [InlineData(FrameLayout.SPEC)]
    public void Invalid_check_is_reported_without_losing_frame_or_payload(FrameLayout layout)
    {
        byte[] start = [0xA5];
        byte[] end = [0x0D, 0x0A];
        byte[] payload = [0x41, 0x42, 0x43];
        var settings = Settings(layout, start, end);
        var wire = settings.EncodeFrame(payload);
        var checkIndex = layout is FrameLayout.PC or FrameLayout.SPC
            ? wire.Length - settings.CheckCodeSize
            : Array.IndexOf(wire, end[0]) + end.Length;
        wire[checkIndex] ^= 0x80;
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();

        decoder.Consume(wire, messages.Add);
        decoder.SilenceElapsed(messages.Add);

        var message = Assert.Single(messages);
        Assert.Equal(wire, message.Frame.ToArray());
        Assert.Equal(payload, message.Payload.ToArray());
        Assert.False(message.CheckValid);
    }

    [Fact]
    public void Empty_payload_with_explicit_end_is_a_complete_frame()
    {
        byte[] end = [0x0D, 0x0A];
        var settings = SerialProtocolSettings.None(FrameLayout.PE, end: end);
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();

        decoder.Consume(end, messages.Add);

        var message = Assert.Single(messages);
        Assert.Equal(end, message.Frame.ToArray());
        Assert.Empty(message.Payload.ToArray());
    }

    [Fact]
    public void Reset_discards_partial_frame_and_scanner_state()
    {
        byte[] end = [0x0D, 0x0A];
        var settings = SerialProtocolSettings.None(FrameLayout.PE, end: end);
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();

        decoder.Consume([0x11, 0x0D], messages.Add);
        Assert.True(decoder.HasPendingData);
        decoder.Reset();
        Assert.False(decoder.HasPendingData);

        decoder.Consume([0x22, .. end], messages.Add);
        var message = Assert.Single(messages);
        Assert.Equal(new byte[] { 0x22 }, message.Payload.ToArray());
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

    [Fact]
    public void Pce_does_not_mistake_multi_byte_end_sequence_in_crc_for_frame_boundary()
    {
        var options = CrcOptions();
        var check = SerialProtocolSettings.Crc16(options, FrameLayout.PC).CreateMessageCheck();
        var payload = FindPayloadWithCrc(check, out var crc);
        var settings = SerialProtocolSettings.Crc16(options, FrameLayout.PCE, end: crc);
        var wire = settings.EncodeFrame(payload);
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();

        foreach (var value in wire) decoder.Consume([value], messages.Add);

        var message = Assert.Single(messages);
        Assert.Equal(wire, message.Frame.ToArray());
        Assert.Equal(payload, message.Payload.ToArray());
        Assert.True(message.CheckValid);
    }

    private static SerialProtocolSettings Settings(FrameLayout layout, byte[] start, byte[] end)
    {
        var hasCheck = layout is FrameLayout.PC or FrameLayout.PEC or FrameLayout.SPC or FrameLayout.SPEC or FrameLayout.PCE;
        return hasCheck
            ? SerialProtocolSettings.Crc16(CrcOptions(), layout, start, end)
            : SerialProtocolSettings.None(layout, start, end);
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

    private static byte[] FindPayloadWithCrc(IMessageCheck check, out byte[] crc)
    {
        for (var value = 0; value <= ushort.MaxValue; value++)
        {
            byte[] payload = [(byte)value, (byte)(value >> 8)];
            var checkedPayload = check.Append(payload);
            crc = checkedPayload[^check.FieldLength..];
            if (!ContainsSequence(payload, crc)) return payload;
        }
        throw new InvalidOperationException("Unable to synthesize a payload with a distinct two-byte CRC.");
    }

    private static bool ContainsSequence(IReadOnlyList<byte> source, IReadOnlyList<byte> sequence)
    {
        for (var i = 0; i <= source.Count - sequence.Count; i++)
        {
            var match = true;
            for (var j = 0; j < sequence.Count; j++)
                if (source[i + j] != sequence[j]) { match = false; break; }
            if (match) return true;
        }
        return false;
    }
}

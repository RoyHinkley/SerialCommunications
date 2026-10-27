using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class SilenceFramedReceiveTests
{
    private static CrcOptions FieldCrc() => new()
    {
        Polynomial = 0xA001,
        InitialValue = 0xFFFF,
        ExpectedResidue = 0x0000,
        PostInvert = false,
        MsBitFirst = false,
        MsByteFirst = false
    };

    private static SerialChannel CrcChannel(CrcOptions options) => new(new SerialChannelConfiguration(new SerialPortSettings("TEST", 115200))
    {
        ProtocolSettings = SerialProtocolSettings.Crc16(options, FrameLayout.PC)
    });

    [Fact]
    public void Valid_crc_response_preserves_wire_payload_and_crc_evidence()
    {
        using var device = CrcChannel(FieldCrc());
        var payload = new byte[] { 0x02, 0x03, 0x10, 0x03, 0x20 };
        var wire = new Crc(FieldCrc()).Append(payload);

        var received = device.DecodeSilenceFramedMessage(wire);

        Assert.True(received.CheckValid);
        Assert.Equal(wire, received.WireBytes.ToArray());
        Assert.Equal(payload, received.PayloadBytes.ToArray());
        Assert.Equal(wire[^2..], received.CheckBytes.ToArray());
        Assert.Equal((ushort)0x0000, received.CalculatedResidue);
        Assert.Equal((ushort)0x0000, received.ExpectedResidue);
    }

    [Fact]
    public void Corrupt_crc_is_reported_without_losing_received_bytes()
    {
        using var device = CrcChannel(FieldCrc());
        var payload = new byte[] { 0x02, 0x03, 0x10, 0x20 };
        var wire = new Crc(FieldCrc()).Append(payload);
        wire[^1] ^= 0x01;

        var received = device.DecodeSilenceFramedMessage(wire);

        Assert.False(received.CheckValid);
        Assert.Equal(wire, received.WireBytes.ToArray());
        Assert.Equal(payload, received.PayloadBytes.ToArray());
        Assert.NotEqual(received.ExpectedResidue, received.CalculatedResidue);
    }

    [Fact]
    public void No_crc_configuration_delivers_all_bytes_as_payload()
    {
        using var device = new SerialChannel("TEST");
        var wire = new byte[] { 0x02, 0x03, 0x00, 0xFF, 0x03 };

        var received = device.DecodeSilenceFramedMessage(wire);

        Assert.Null(received.CheckValid);
        Assert.Equal(wire, received.WireBytes.ToArray());
        Assert.Equal(wire, received.PayloadBytes.ToArray());
        Assert.Empty(received.CheckBytes.ToArray());
    }

    [Fact]
    public void Virtual_instrument_response_with_embedded_03_decodes_as_one_silence_framed_message()
    {
        var crcOptions = FieldCrc();
        var command = new byte[] { 0x02, 0x03, 0x00, 0x01, 0x00, 0x02 };
        var payload = new byte[] { 0x02, 0x03, 0x11, 0x03, 0x22, 0x33 };
        var response = new Crc(crcOptions).Append(payload);
        var instrument = new VirtualInstrument().RespondTo(command, response);
        using var device = CrcChannel(crcOptions);

        var received = device.DecodeSilenceFramedMessage(instrument.Respond(command));

        Assert.True(received.CheckValid);
        Assert.Equal(payload, received.PayloadBytes.ToArray());
    }

    [Fact]
    public void Every_possible_transport_fragmentation_reassembles_to_same_message()
    {
        var options = FieldCrc();
        var payload = new byte[] { 0x02, 0x03, 0x11, 0x22, 0x03, 0x44 };
        var wire = new Crc(options).Append(payload);
        using var device = CrcChannel(options);

        for (var split = 1; split < wire.Length; split++)
        {
            var fragments = VirtualInstrument.Fragment(wire, split);
            var reassembled = fragments.SelectMany(fragment => fragment).ToArray();
            var received = device.DecodeSilenceFramedMessage(reassembled);

            Assert.True(received.CheckValid);
            Assert.Equal(payload, received.PayloadBytes.ToArray());
        }
    }

    [Fact]
    public void Consecutive_virtual_instrument_responses_decode_independently()
    {
        var options = FieldCrc();
        var command1 = new byte[] { 0x01 };
        var command2 = new byte[] { 0x02 };
        var payload1 = new byte[] { 0x10, 0x03, 0x20 };
        var payload2 = new byte[] { 0x30, 0x40, 0x03, 0x50 };
        var instrument = new VirtualInstrument()
            .RespondTo(command1, new Crc(options).Append(payload1))
            .RespondTo(command2, new Crc(options).Append(payload2));
        using var device = CrcChannel(options);

        var first = device.DecodeSilenceFramedMessage(instrument.Respond(command1));
        var second = device.DecodeSilenceFramedMessage(instrument.Respond(command2));

        Assert.True(first.CheckValid);
        Assert.True(second.CheckValid);
        Assert.Equal(payload1, first.PayloadBytes.ToArray());
        Assert.Equal(payload2, second.PayloadBytes.ToArray());
    }

    [Fact]
    public void Crc_failure_does_not_poison_following_valid_message()
    {
        var options = FieldCrc();
        var badPayload = new byte[] { 0x10, 0x20 };
        var goodPayload = new byte[] { 0x30, 0x03, 0x40 };
        var badWire = new Crc(options).Append(badPayload);
        badWire[^1] ^= 0x80;
        var goodWire = new Crc(options).Append(goodPayload);
        using var device = CrcChannel(options);

        var bad = device.DecodeSilenceFramedMessage(badWire);
        var good = device.DecodeSilenceFramedMessage(goodWire);

        Assert.False(bad.CheckValid);
        Assert.True(good.CheckValid);
        Assert.Equal(goodPayload, good.PayloadBytes.ToArray());
    }

    [Fact]
    public void Crc_byte_equal_to_03_has_no_framing_significance_in_silence_mode()
    {
        var options = FieldCrc();
        var payload = FindPayloadWhoseCrcContains(0x03, options);
        var wire = new Crc(options).Append(payload);
        using var device = CrcChannel(options);

        var received = device.DecodeSilenceFramedMessage(wire);

        Assert.Contains((byte)0x03, received.CheckBytes.ToArray());
        Assert.True(received.CheckValid);
        Assert.Equal(payload, received.PayloadBytes.ToArray());
    }

    private static byte[] FindPayloadWhoseCrcContains(byte value, CrcOptions options)
    {
        for (var i = 0; i <= ushort.MaxValue; i++)
        {
            var payload = new byte[] { (byte)i, (byte)(i >> 8) };
            var wire = new Crc(options).Append(payload);
            if (wire[^2] == value || wire[^1] == value) return payload;
        }

        throw new InvalidOperationException($"Unable to synthesize a CRC containing {value:X2}.");
    }
}

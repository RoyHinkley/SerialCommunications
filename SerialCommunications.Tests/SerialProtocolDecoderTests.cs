using SerialCommunications.Serial;
using SerialCommunications.Serial.Protocol;

namespace SerialCommunications.Tests;

public sealed class SerialProtocolDecoderTests
{
    [Fact]
    public void Aeon_settings_decode_crc_terminated_message_and_preserve_complete_frame()
    {
        var options = TestCrcOptions(omitTermChar: false);
        var settings = SerialProtocolSettings.From(options, suppressInvalidMessages: false);
        var decoder = settings.CreateMessageDecoder();
        var payload = new byte[] { (byte)'A', 0x0D, 0x0A, (byte)'B', 0x0A };
        var wire = new Crc(options).Append(payload);
        DecodedMessage? decoded = null;

        decoder.Consume(wire, message => decoded = message);

        Assert.NotNull(decoded);
        Assert.True(decoded!.CheckValid);
        Assert.Equal(wire, decoded.Frame.ToArray());
        Assert.Equal(payload, decoded.Payload.ToArray());
    }

    [Fact]
    public void Crc_without_terminator_uses_explicit_silence_boundary()
    {
        var options = TestCrcOptions(omitTermChar: true);
        var settings = SerialProtocolSettings.From(options, suppressInvalidMessages: false);
        var decoder = settings.CreateMessageDecoder();
        var payload = new byte[] { 0x10, 0x20, 0x30 };
        var wire = new Crc(options).Append(payload);
        DecodedMessage? decoded = null;

        decoder.Consume(wire, message => decoded = message);
        Assert.Null(decoded);

        decoder.SilenceElapsed(message => decoded = message);
        Assert.NotNull(decoded);
        Assert.True(decoded!.CheckValid);
        Assert.Equal(wire, decoded.Frame.ToArray());
        Assert.Equal(payload, decoded.Payload.ToArray());
    }

    [Fact]
    public void No_crc_legacy_settings_use_silence_and_preserve_all_bytes()
    {
        var settings = SerialProtocolSettings.From(null, suppressInvalidMessages: false);
        var decoder = settings.CreateMessageDecoder();
        var payload = new byte[] { 0x41, 0x0D, 0x0A, 0x03, 0x00, 0x42 };
        DecodedMessage? decoded = null;

        decoder.Consume(payload, message => decoded = message);
        Assert.Null(decoded);

        decoder.SilenceElapsed(message => decoded = message);
        Assert.NotNull(decoded);
        Assert.True(decoded!.CheckValid);
        Assert.Equal(payload, decoded.Frame.ToArray());
        Assert.Equal(payload, decoded.Payload.ToArray());
    }

    private static CrcOptions TestCrcOptions(bool omitTermChar) => new()
    {
        Polynomial = 0xA001,
        InitialValue = 0xFFFF,
        ExpectedResidue = 0,
        PostInvert = false,
        MsBitFirst = false,
        MsByteFirst = false,
        TermChar = 0x03,
        OmitTermChar = omitTermChar
    };
}

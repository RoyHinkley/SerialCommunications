using SerialCommunications.Serial;
using SerialCommunications.Serial.Protocol;

namespace SerialCommunications.Tests;

public sealed class ReceiveFailureSemanticsTests
{
    [Theory]
    [InlineData(FrameLayout.PE)]
    [InlineData(FrameLayout.SPE)]
    public void Missing_explicit_end_is_emitted_as_framing_invalid_on_silence(FrameLayout layout)
    {
        byte[] start = [0xA5];
        byte[] end = [0x0D, 0x0A];
        var settings = SerialProtocolSettings.None(layout, start, end);
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();
        byte[] partial = layout == FrameLayout.SPE ? [0xA5, 0x41, 0x42, 0x0D] : [0x41, 0x42, 0x0D];

        decoder.Consume(partial, messages.Add);
        Assert.Empty(messages);
        Assert.True(decoder.HasPendingData);

        decoder.SilenceElapsed(messages.Add);

        var message = Assert.Single(messages);
        Assert.False(message.FramingValid);
        Assert.Equal(partial, message.Frame.ToArray());
        Assert.False(decoder.HasPendingData);
    }

    [Fact]
    public void Incomplete_check_after_explicit_end_is_framing_invalid_on_silence()
    {
        byte[] end = [0x03];
        var settings = SerialProtocolSettings.Crc16(TestCrc(), FrameLayout.PEC, end: end);
        var wire = settings.EncodeFrame([0x41, 0x42]);
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();
        var partial = wire[..^1];

        decoder.Consume(partial, messages.Add);
        Assert.Empty(messages);
        decoder.SilenceElapsed(messages.Add);

        var message = Assert.Single(messages);
        Assert.False(message.FramingValid);
        Assert.Equal(partial, message.Frame.ToArray());
        Assert.False(decoder.HasPendingData);
    }

    [Fact]
    public void Invalid_crc_retains_generic_check_evidence()
    {
        var settings = SerialProtocolSettings.Crc16(TestCrc(), FrameLayout.PC);
        var wire = settings.EncodeFrame([0x10, 0x20, 0x30]);
        wire[^1] ^= 0x01;
        var decoder = new LayoutAwareMessageDecoder(settings);
        DecodedMessage? decoded = null;

        decoder.Consume(wire, message => decoded = message);
        decoder.SilenceElapsed(message => decoded = message);
        var data = ReceivedDataAdapter.Create(Assert.IsType<DecodedMessage>(decoded), settings);

        Assert.True(data.FramingValid);
        Assert.False(data.CheckValid);
        Assert.Equal(wire[^2..], data.CheckBytes.ToArray());
        Assert.NotNull(data.ReceivedCheckCode);
        Assert.NotNull(data.CalculatedResidue);
        Assert.Equal(settings.ExpectedResidue, data.ExpectedResidue);
    }

    private static CrcOptions TestCrc() => new()
    {
        Polynomial = 0xA001,
        InitialValue = 0xFFFF,
        ExpectedResidue = 0,
        PostInvert = false,
        MsBitFirst = false,
        MsByteFirst = false
    };
}

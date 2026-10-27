using SerialCommunications.Serial;
using SerialCommunications.Serial.Protocol;

namespace SerialCommunications.Tests;

public sealed class IncompleteFrameTests
{
    public static TheoryData<FrameLayout> ExplicitEndLayouts => new()
    {
        FrameLayout.PE, FrameLayout.PEC, FrameLayout.SPE, FrameLayout.SPEC, FrameLayout.PCE
    };

    [Theory]
    [MemberData(nameof(ExplicitEndLayouts))]
    public void Silence_resolves_missing_explicit_end_as_framing_invalid(FrameLayout layout)
    {
        byte[] start = [0xA5];
        byte[] end = [0x0D, 0x0A];
        byte[] bytes = layout is FrameLayout.SPE or FrameLayout.SPEC
            ? [.. start, 0x41, 0x42]
            : [0x41, 0x42];
        var settings = HasCheck(layout)
            ? SerialProtocolSettings.Crc16(TestCrc(), layout, start, end)
            : SerialProtocolSettings.None(layout, start, end);
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();

        decoder.Consume(bytes, messages.Add);
        Assert.True(decoder.HasPendingData);
        decoder.SilenceElapsed(messages.Add);

        var message = Assert.Single(messages);
        Assert.Equal(bytes, message.Frame.ToArray());
        Assert.Equal(new byte[] { 0x41, 0x42 }, message.Payload.ToArray());
        Assert.False(message.FramingValid);
        Assert.False(message.CheckValid);
        Assert.False(decoder.HasPendingData);
    }

    [Theory]
    [InlineData(FrameLayout.PEC)]
    [InlineData(FrameLayout.SPEC)]
    public void Silence_resolves_end_with_incomplete_check_as_framing_invalid(FrameLayout layout)
    {
        byte[] start = [0xA5];
        byte[] end = [0x03];
        byte[] payload = [0x41, 0x42];
        var settings = SerialProtocolSettings.Crc16(TestCrc(), layout, start, end);
        var complete = settings.EncodeFrame(payload);
        var incomplete = complete[..^1];
        var decoder = new LayoutAwareMessageDecoder(settings);
        var messages = new List<DecodedMessage>();

        decoder.Consume(incomplete, messages.Add);
        Assert.Empty(messages);
        decoder.SilenceElapsed(messages.Add);

        var message = Assert.Single(messages);
        Assert.Equal(incomplete, message.Frame.ToArray());
        Assert.False(message.FramingValid);
        Assert.False(decoder.HasPendingData);
    }

    private static bool HasCheck(FrameLayout layout) =>
        layout is FrameLayout.PEC or FrameLayout.SPEC or FrameLayout.PCE;

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

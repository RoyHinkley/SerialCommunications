using SerialCommunications.Serial;
using SerialCommunications.Serial.Protocol;

namespace SerialCommunications.Tests;

public sealed class SerialChannelFramingTests
{
    public static TheoryData<FrameLayout> Layouts => new()
    {
        FrameLayout.P, FrameLayout.PE, FrameLayout.PC, FrameLayout.PEC,
        FrameLayout.SP, FrameLayout.SPE, FrameLayout.SPC, FrameLayout.SPEC, FrameLayout.PCE
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Command_writes_exact_frame_for_each_layout(FrameLayout layout)
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var channel = new SerialChannel(new SerialPortSettings("COM_TEST"), factory);
        byte[] start = [0xA5, 0x5A];
        byte[] end = [0x0D, 0x0A];
        byte[] payload = [0x41, 0x42, 0x43];
        var settings = Settings(layout, start, end);

        Assert.True(channel.StageProtocolSettings(settings));
        Assert.True(channel.Connect());
        Assert.True(channel.Command(payload));
        Assert.True(SpinWait.SpinUntil(() => transport.Writes.Count != 0, TimeSpan.FromSeconds(2)));

        var write = Assert.Single(transport.Writes);
        Assert.Equal(settings.EncodeFrame(payload), write);
        Assert.True(channel.Disconnect());
    }

    private static SerialProtocolSettings Settings(FrameLayout layout, byte[] start, byte[] end)
    {
        var hasCheck = layout is FrameLayout.PC or FrameLayout.PEC or FrameLayout.SPC or FrameLayout.SPEC or FrameLayout.PCE;
        return hasCheck
            ? SerialProtocolSettings.Crc16(new CrcOptions(), layout, start, end)
            : SerialProtocolSettings.None(layout, start, end);
    }
}

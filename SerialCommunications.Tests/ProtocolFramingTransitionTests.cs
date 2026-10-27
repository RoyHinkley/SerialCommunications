using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class ProtocolFramingTransitionTests
{
    [Fact]
    public void Protocol_change_applies_immediately_when_receiver_is_idle()
    {
        var oldSettings = AeonSettings(FrameLayout.PC);
        var newSettings = AeonSettings(FrameLayout.PCE);
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var device = new SerialChannel(new SerialChannelConfiguration(new SerialPortSettings("TEST", 115200))
        {
            ProtocolSettings = oldSettings,
            MaximumMillisecondsSilenceInMessage = 5
        }, factory);

        var received = new List<ReceivedData>();
        var sync = new object();
        device.DataReceived += data => { lock (sync) { received.Add(data); Monitor.PulseAll(sync); } };
        Assert.True(device.Connect());

        var oldPayload = new byte[] { 0x41, 0x42, 0x43 };
        transport.EnqueueReceived(oldSettings.EncodeFrame(oldPayload));
        transport.RaiseDataReceived();
        WaitForMessages(received, sync, 1);

        Assert.True(device.StageProtocolSettings(newSettings));
        Assert.Equal(FrameLayout.PCE, device.ProtocolSettings.Layout);
        Assert.Null(device.PendingProtocolSettings);

        var newPayload = new byte[] { 0x51, 0x52, 0x53 };
        var newWire = newSettings.EncodeFrame(newPayload);
        Assert.Equal(0x03, newWire[^1]);
        transport.EnqueueReceived(newWire);
        transport.RaiseDataReceived();
        var messages = WaitForMessages(received, sync, 2);

        Assert.Equal(newPayload, messages[1].PayloadBytes.ToArray());
        Assert.True(messages[1].CheckValid);
    }

    [Fact]
    public void Protocol_change_updates_transmit_frame_for_subsequent_commands()
    {
        var oldSettings = AeonSettings(FrameLayout.PC);
        var newOptions = AeonOptions();
        newOptions.Polynomial = 0xA001;
        newOptions.ExpectedResidue = 0;
        newOptions.PostInvert = false;
        var newSettings = SerialProtocolSettings.Crc16(newOptions, FrameLayout.PCE, end: new byte[] { 0x03 });
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var device = new SerialChannel(new SerialChannelConfiguration(new SerialPortSettings("TEST", 115200))
        {
            ProtocolSettings = oldSettings
        }, factory);
        Assert.True(device.Connect());

        var firstPayload = new byte[] { 0x41, 0x42, 0x43 };
        Assert.True(device.Command(firstPayload));
        WaitForWrites(transport, 1);
        Assert.Equal(oldSettings.EncodeFrame(firstPayload), transport.Writes[0]);

        Assert.True(device.StageProtocolSettings(newSettings));
        Assert.Equal(newOptions.Polynomial, device.ProtocolSettings.Polynomial);
        Assert.Null(device.PendingProtocolSettings);

        var secondPayload = new byte[] { 0x51, 0x52, 0x53 };
        Assert.True(device.Command(secondPayload));
        WaitForWrites(transport, 2);
        Assert.Equal(newSettings.EncodeFrame(secondPayload), transport.Writes[1]);
    }

    [Fact]
    public void Staged_decoder_applies_to_bytes_after_boundary_in_same_transport_chunk()
    {
        var oldSettings = AeonSettings(FrameLayout.PCE);
        var newOptions = AeonOptions();
        newOptions.Polynomial = 0xA001;
        newOptions.ExpectedResidue = 0;
        newOptions.PostInvert = false;
        var newSettings = SerialProtocolSettings.Crc16(newOptions, FrameLayout.PCE, end: new byte[] { 0x03 });

        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var device = new SerialChannel(new SerialChannelConfiguration(new SerialPortSettings("TEST", 115200))
        {
            ProtocolSettings = oldSettings
        }, factory);
        var received = new List<ReceivedData>();
        var sync = new object();
        device.DataReceived += data => { lock (sync) { received.Add(data); Monitor.PulseAll(sync); } };
        Assert.True(device.Connect());

        var oldPayload = new byte[] { 0x41, 0x42, 0x43, 0x44 };
        var oldWire = oldSettings.EncodeFrame(oldPayload);
        var newPayload = new byte[] { 0x51, 0x52, 0x53, 0x54 };
        var newWire = newSettings.EncodeFrame(newPayload);

        transport.EnqueueReceived(oldWire[..2]);
        transport.RaiseDataReceived();
        WaitUntilDrained(transport);
        Assert.True(device.StageProtocolSettings(newSettings));
        Assert.NotNull(device.PendingProtocolSettings);

        transport.EnqueueReceived(oldWire[2..].Concat(newWire).ToArray());
        transport.RaiseDataReceived();
        var messages = WaitForMessages(received, sync, 2);

        Assert.True(messages[0].CheckValid);
        Assert.Equal(oldPayload, messages[0].PayloadBytes.ToArray());
        Assert.True(messages[1].CheckValid);
        Assert.Equal(newPayload, messages[1].PayloadBytes.ToArray());
        Assert.Equal(newOptions.Polynomial, device.ProtocolSettings.Polynomial);
        Assert.Null(device.PendingProtocolSettings);
    }

    private static SerialProtocolSettings AeonSettings(FrameLayout layout) =>
        SerialProtocolSettings.Crc16(AeonOptions(), layout, end: layout == FrameLayout.PCE ? new byte[] { 0x03 } : []);

    private static CrcOptions AeonOptions() => new()
    {
        Polynomial = 0xDAAE,
        InitialValue = 0xFFFF,
        ExpectedResidue = 0x82C0,
        PostInvert = true,
        MsBitFirst = false,
        MsByteFirst = false
    };

    private static void WaitUntilDrained(FakeSerialTransport transport)
    {
        var deadline = Environment.TickCount64 + 2000;
        while (transport.BytesToRead != 0 && Environment.TickCount64 < deadline) Thread.Yield();
        if (transport.BytesToRead != 0) throw new TimeoutException("Receive transport did not drain.");
    }

    private static void WaitForWrites(FakeSerialTransport transport, int count)
    {
        if (!SpinWait.SpinUntil(() => transport.Writes.Count >= count, TimeSpan.FromSeconds(2)))
            throw new TimeoutException($"Expected {count} writes; observed {transport.Writes.Count}.");
    }

    private static IReadOnlyList<ReceivedData> WaitForMessages(List<ReceivedData> messages, object sync, int count)
    {
        var deadline = Environment.TickCount64 + 2000;
        lock (sync)
        {
            while (messages.Count < count)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) throw new TimeoutException($"Expected {count} messages; received {messages.Count}.");
                Monitor.Wait(sync, (int)Math.Min(remaining, int.MaxValue));
            }
            return messages.ToArray();
        }
    }
}

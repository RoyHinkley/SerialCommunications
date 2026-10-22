using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class ProtocolFramingTransitionTests
{
    [Fact]
    public void Protocol_change_applies_immediately_when_receiver_is_idle()
    {
        var oldOptions = AeonOptions(omitTermChar: true);
        var newOptions = AeonOptions(omitTermChar: false);
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var device = new SerialChannel(new SerialChannelConfiguration(new SerialPortSettings("TEST", 115200))
        {
            ProtocolSettings = SerialProtocolSettings.From(oldOptions, false),
            MaximumMillisecondsSilenceInMessage = 5
        }, factory);

        var received = new List<ReceivedData>();
        var sync = new object();
        device.DataReceived += data => { lock (sync) { received.Add(data); Monitor.PulseAll(sync); } };
        Assert.True(device.Connect());

        var oldPayload = new byte[] { 0x41, 0x42, 0x43 };
        transport.EnqueueReceived(new Crc(oldOptions).Append(oldPayload));
        transport.RaiseDataReceived();
        WaitForMessages(received, sync, 1);

        Assert.True(device.StageProtocolSettings(SerialProtocolSettings.From(newOptions, false)));
        Assert.False(device.ProtocolSettings.OmitTermChar);
        Assert.Null(device.PendingProtocolSettings);

        var newPayload = new byte[] { 0x51, 0x52, 0x53 };
        var newWire = new Crc(newOptions).Append(newPayload);
        Assert.Equal(newOptions.TermChar, newWire[^1]);
        transport.EnqueueReceived(newWire);
        transport.RaiseDataReceived();
        var messages = WaitForMessages(received, sync, 2);

        Assert.Equal(newPayload, messages[1].PayloadBytes.ToArray());
        Assert.True(messages[1].CrcValid);
    }

    [Fact]
    public void Staged_decoder_applies_to_bytes_after_boundary_in_same_transport_chunk()
    {
        var oldOptions = AeonOptions(omitTermChar: false);
        var newOptions = AeonOptions(omitTermChar: false);
        newOptions.Polynomial = 0xA001;
        newOptions.ExpectedResidue = 0;
        newOptions.PostInvert = false;

        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var device = new SerialChannel(new SerialChannelConfiguration(new SerialPortSettings("TEST", 115200))
        {
            ProtocolSettings = SerialProtocolSettings.From(oldOptions, false)
        }, factory);
        var received = new List<ReceivedData>();
        var sync = new object();
        device.DataReceived += data => { lock (sync) { received.Add(data); Monitor.PulseAll(sync); } };
        Assert.True(device.Connect());

        var oldPayload = new byte[] { 0x41, 0x42, 0x43, 0x44 };
        var oldWire = new Crc(oldOptions).Append(oldPayload);
        var newPayload = new byte[] { 0x51, 0x52, 0x53, 0x54 };
        var newWire = new Crc(newOptions).Append(newPayload);

        transport.EnqueueReceived(oldWire[..2]);
        transport.RaiseDataReceived();
        WaitUntilDrained(transport);
        Assert.True(device.StageProtocolSettings(SerialProtocolSettings.From(newOptions, false)));
        Assert.NotNull(device.PendingProtocolSettings);

        transport.EnqueueReceived(oldWire[2..].Concat(newWire).ToArray());
        transport.RaiseDataReceived();
        var messages = WaitForMessages(received, sync, 2);

        Assert.True(messages[0].CrcValid);
        Assert.Equal(oldPayload, messages[0].PayloadBytes.ToArray());
        Assert.True(messages[1].CrcValid);
        Assert.Equal(newPayload, messages[1].PayloadBytes.ToArray());
        Assert.Equal(newOptions.Polynomial, device.ProtocolSettings.Polynomial);
        Assert.Null(device.PendingProtocolSettings);
    }

    private static CrcOptions AeonOptions(bool omitTermChar) => new()
    {
        Polynomial = 0xDAAE,
        InitialValue = 0xFFFF,
        ExpectedResidue = 0x82C0,
        PostInvert = true,
        MsBitFirst = false,
        MsByteFirst = false,
        TermChar = 0x03,
        OmitTermChar = omitTermChar
    };

    private static void WaitUntilDrained(FakeSerialTransport transport)
    {
        var deadline = Environment.TickCount64 + 2000;
        while (transport.BytesToRead != 0 && Environment.TickCount64 < deadline) Thread.Yield();
        if (transport.BytesToRead != 0) throw new TimeoutException("Receive transport did not drain.");
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

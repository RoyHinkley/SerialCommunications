using System.Diagnostics;
using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class SerialChannelLiveTimingTests
{
    private static SerialPortSettings Settings() => new("TEST", 115200);

    [Fact]
    public void Message_pacing_change_applies_to_next_message_without_reconnect()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory) { MillisecondsBetweenMessages = 500 };
        Assert.True(device.Connect());

        Assert.True(device.Command(new byte[] { 0x11 }));
        Assert.True(SpinWait.SpinUntil(() => transport.Writes.Count == 1, TimeSpan.FromSeconds(2)));

        device.MillisecondsBetweenMessages = 0;
        var sw = Stopwatch.StartNew();
        Assert.True(device.Command(new byte[] { 0x22 }));
        Assert.True(SpinWait.SpinUntil(() => transport.Writes.Count == 2, TimeSpan.FromMilliseconds(250)));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250));
        Assert.Equal(new byte[] { 0x22 }, transport.Writes[1]);
    }

    [Fact]
    public void Byte_pacing_change_does_not_retime_message_already_in_progress()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory) { MillisecondsBetweenBytes = 80 };
        Assert.True(device.Connect());

        Assert.True(device.Command(new byte[] { 0x11, 0x12, 0x13 }));
        Assert.True(SpinWait.SpinUntil(() => transport.Writes.Count == 1, TimeSpan.FromSeconds(2)));
        device.MillisecondsBetweenBytes = 0;

        Thread.Sleep(35);
        Assert.Single(transport.Writes);
        Assert.True(SpinWait.SpinUntil(() => transport.Writes.Count == 3, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Byte_pacing_change_applies_to_next_message_without_reconnect()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory) { MillisecondsBetweenBytes = 60 };
        Assert.True(device.Connect());

        Assert.True(device.Command(new byte[] { 0x11, 0x12 }));
        Assert.True(SpinWait.SpinUntil(() => transport.Writes.Count == 2, TimeSpan.FromSeconds(1)));

        device.MillisecondsBetweenBytes = 0;
        Assert.True(device.Command(new byte[] { 0x21, 0x22, 0x23 }));
        Assert.True(SpinWait.SpinUntil(() => transport.Writes.Count == 5, TimeSpan.FromMilliseconds(250)));
        Assert.Equal(new byte[] { 0x21 }, transport.Writes[2]);
        Assert.Equal(new byte[] { 0x22 }, transport.Writes[3]);
        Assert.Equal(new byte[] { 0x23 }, transport.Writes[4]);
    }

    [Fact]
    public void Silence_change_applies_to_next_received_message_without_reconnect()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory) { MaximumMillisecondsSilenceInMessage = 300 };
        var delivered = new List<byte[]>();
        var sync = new object();
        device.DataReceived += data => { lock (sync) delivered.Add(data.PayloadBytes.ToArray()); };
        Assert.True(device.Connect());

        transport.EnqueueReceived(0x11);
        transport.RaiseDataReceived();
        Assert.True(SpinWait.SpinUntil(() => { lock (sync) return delivered.Count == 1; }, TimeSpan.FromSeconds(1)));

        device.MaximumMillisecondsSilenceInMessage = 20;
        transport.EnqueueReceived(0x21);
        transport.RaiseDataReceived();
        Assert.True(SpinWait.SpinUntil(() => { lock (sync) return delivered.Count == 2; }, TimeSpan.FromMilliseconds(200)));
        lock (sync) Assert.Equal(new byte[] { 0x21 }, delivered[1]);
    }

    [Fact]
    public void Silence_change_does_not_retime_message_already_in_progress()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory) { MaximumMillisecondsSilenceInMessage = 250 };
        var delivered = 0;
        device.DataReceived += _ => Interlocked.Increment(ref delivered);
        Assert.True(device.Connect());

        transport.EnqueueReceived(0x31);
        transport.RaiseDataReceived();
        Assert.True(SpinWait.SpinUntil(() => device.Telemetry.BytesReceived >= 1, TimeSpan.FromSeconds(1)));
        device.MaximumMillisecondsSilenceInMessage = 10;

        Thread.Sleep(60);
        Assert.Equal(0, Volatile.Read(ref delivered));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref delivered) == 1, TimeSpan.FromSeconds(1)));
    }
}

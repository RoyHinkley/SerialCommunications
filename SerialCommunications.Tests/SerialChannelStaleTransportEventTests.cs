using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class SerialChannelStaleTransportEventTests
{
    [Fact]
    public void Delayed_receive_notification_from_released_transport_cannot_drive_replacement_session()
    {
        var factory = new FakeSerialTransportFactory();
        var original = factory.Enqueue();
        var replacement = factory.Enqueue();
        using var device = new SerialChannel(new SerialPortSettings("TEST", 115200), factory)
        {
            RecoveryPolicy = new SerialRecoveryPolicy(TimeSpan.FromMilliseconds(20)),
            MaximumMillisecondsSilenceInMessage = 20
        };
        var delivered = new List<byte[]>();
        var sync = new object();
        device.DataReceived += data => { lock (sync) delivered.Add(data.PayloadBytes.ToArray()); };

        Assert.True(device.Connect());
        var delayedOldNotification = original.CaptureDataReceivedNotification();
        original.IsAvailable = false;
        Assert.True(SpinWait.SpinUntil(
            () => factory.Created.Count >= 2 && device.ConnectionState == SerialConnectionState.Connected,
            TimeSpan.FromSeconds(2)));

        var notificationsBefore = device.Telemetry.ReceiveNotifications;
        replacement.EnqueueReceived(0x51, 0x52);
        delayedOldNotification();
        Thread.Sleep(75);

        Assert.Equal(notificationsBefore, device.Telemetry.ReceiveNotifications);
        Assert.Equal(0, replacement.ReadCalls);
        lock (sync) Assert.Empty(delivered);

        replacement.RaiseDataReceived();
        Assert.True(SpinWait.SpinUntil(() => { lock (sync) return delivered.Count == 1; }, TimeSpan.FromSeconds(2)));
        lock (sync) Assert.Equal(new byte[] { 0x51, 0x52 }, delivered[0]);
    }

    [Fact]
    public void Delayed_pin_notification_from_released_transport_cannot_publish_replacement_signals()
    {
        var factory = new FakeSerialTransportFactory();
        var original = factory.Enqueue();
        var replacement = factory.Enqueue();
        replacement.RtsEnable = true;
        replacement.CtsHolding = true;
        using var device = new SerialChannel(new SerialPortSettings("TEST", 115200), factory)
        {
            RecoveryPolicy = new SerialRecoveryPolicy(TimeSpan.FromMilliseconds(20))
        };
        var signals = new List<SerialSignalState>();
        var sync = new object();
        device.SignalsChanged += state => { lock (sync) signals.Add(state); };

        Assert.True(device.Connect());
        var delayedOldNotification = original.CapturePinChangedNotification();
        original.IsAvailable = false;
        Assert.True(SpinWait.SpinUntil(
            () => factory.Created.Count >= 2 && device.ConnectionState == SerialConnectionState.Connected,
            TimeSpan.FromSeconds(2)));
        lock (sync) signals.Clear();

        delayedOldNotification();
        Thread.Sleep(50);
        lock (sync) Assert.Empty(signals);

        replacement.CtsHolding = false;
        replacement.RaisePinChanged();
        Assert.True(SpinWait.SpinUntil(() => { lock (sync) return signals.Count == 1; }, TimeSpan.FromSeconds(2)));
        lock (sync)
        {
            Assert.True(signals[0].Rts);
            Assert.False(signals[0].Cts);
        }
    }
}

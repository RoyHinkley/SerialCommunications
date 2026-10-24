using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class SerialChannelStuckWriteIsolationTests
{
    private static SerialPortSettings Settings() => new("TEST", 115200);
    private static SerialRecoveryPolicy FastRecovery() => new(TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task Disconnect_abandons_uninterruptible_old_transmit_worker_without_contaminating_next_session()
    {
        var factory = new FakeSerialTransportFactory();
        var original = factory.Enqueue();
        var replacement = factory.Enqueue();
        using var oldWriteGate = new ManualResetEventSlim(false);
        original.WriteGate = oldWriteGate;
        original.CloseInterruptsBlockedWrite = false;
        original.DisposeInterruptsBlockedWrite = false;
        using var device = new SerialChannel(Settings(), factory);

        Assert.True(device.Connect());
        Assert.True(device.Command(new byte[] { 0x11 }));
        Assert.True(original.WriteEntered.Wait(TimeSpan.FromSeconds(2)));

        var disconnect = Task.Run(device.Disconnect);
        Assert.False(await disconnect.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(SerialConnectionState.Disconnected, device.ConnectionState);
        Assert.False(device.ConnectionRequested);
        Assert.Equal(1, original.CloseCalls);
        Assert.Equal(1, original.DisposeCalls);
        Assert.False(original.WriteExited.IsSet);

        Assert.True(device.Connect());
        Assert.True(device.Command(new byte[] { 0x22 }));
        Assert.True(replacement.WriteExited.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, replacement.WriteCalls);
        Assert.False(device.HaveWork);

        oldWriteGate.Set();
        Assert.True(original.WriteExited.Wait(TimeSpan.FromSeconds(2)));

        Assert.Equal(1, original.WriteCalls);
        Assert.Equal(1, replacement.WriteCalls);
        Assert.Equal(SerialConnectionState.Connected, device.ConnectionState);
        Assert.True(device.Ready);
        Assert.False(device.HaveWork);
    }

    [Fact]
    public async Task Dispose_abandons_uninterruptible_transmit_worker_and_reaches_disposed_state()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var writeGate = new ManualResetEventSlim(false);
        transport.WriteGate = writeGate;
        transport.CloseInterruptsBlockedWrite = false;
        transport.DisposeInterruptsBlockedWrite = false;
        var device = new SerialChannel(Settings(), factory);

        Assert.True(device.Connect());
        Assert.True(device.Command(new byte[] { 0x31 }));
        Assert.True(transport.WriteEntered.Wait(TimeSpan.FromSeconds(2)));

        var dispose = Task.Run(device.Dispose);
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SerialConnectionState.Disposed, device.ConnectionState);
        Assert.False(device.ConnectionRequested);
        Assert.False(device.Ready);
        Assert.Equal(1, transport.CloseCalls);
        Assert.Equal(1, transport.DisposeCalls);
        Assert.False(transport.WriteExited.IsSet);

        writeGate.Set();
        Assert.True(transport.WriteExited.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, device.Telemetry.MessagesTransmitted);
    }

    [Fact]
    public void Recovery_abandons_uninterruptible_old_transmit_worker_without_contaminating_replacement_session()
    {
        var factory = new FakeSerialTransportFactory();
        var original = factory.Enqueue();
        var replacement = factory.Enqueue();
        using var oldWriteGate = new ManualResetEventSlim(false);
        original.WriteGate = oldWriteGate;
        original.CloseInterruptsBlockedWrite = false;
        original.DisposeInterruptsBlockedWrite = false;
        using var device = new SerialChannel(Settings(), factory) { RecoveryPolicy = FastRecovery() };

        Assert.True(device.Connect());
        Assert.True(device.Command(new byte[] { 0x41 }));
        Assert.True(original.WriteEntered.Wait(TimeSpan.FromSeconds(2)));
        original.IsAvailable = false;

        Assert.True(SpinWait.SpinUntil(
            () => factory.Created.Count >= 2 && device.ConnectionState == SerialConnectionState.Connected,
            TimeSpan.FromSeconds(3)));
        Assert.True(device.ConnectionRequested);
        Assert.True(device.Ready);
        Assert.False(original.WriteExited.IsSet);

        Assert.True(device.Command(new byte[] { 0x42 }));
        Assert.True(replacement.WriteExited.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, replacement.WriteCalls);
        Assert.Equal(new byte[] { 0x42 }, replacement.Writes.Single());

        oldWriteGate.Set();
        Assert.True(original.WriteExited.Wait(TimeSpan.FromSeconds(2)));

        Assert.Equal(1, original.WriteCalls);
        Assert.Equal(1, replacement.WriteCalls);
        Assert.Equal(1, device.Telemetry.MessagesTransmitted);
        Assert.Equal(SerialConnectionState.Connected, device.ConnectionState);
        Assert.False(device.HaveWork);
    }
}

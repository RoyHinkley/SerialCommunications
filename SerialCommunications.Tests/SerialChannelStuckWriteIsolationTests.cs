using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class SerialChannelStuckWriteIsolationTests
{
    private static SerialPortSettings Settings() => new("TEST", 115200);

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
}

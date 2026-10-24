using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class SerialChannelBlockedOpenIsolationTests
{
    private static SerialPortSettings Settings() => new("TEST", 115200);
    private static SerialRecoveryPolicy FastRecovery() => new(TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task Disconnect_can_abandon_blocked_initial_open_without_waiting_for_transport_open()
    {
        var factory = new FakeSerialTransportFactory();
        var blocked = factory.Enqueue();
        using var openGate = new ManualResetEventSlim(false);
        blocked.OpenGate = openGate;
        using var device = new SerialChannel(Settings(), factory);

        var connect = Task.Run(device.Connect);
        Assert.True(blocked.OpenEntered.Wait(TimeSpan.FromSeconds(2)));

        var disconnect = Task.Run(device.Disconnect);
        Assert.True(await disconnect.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await disconnect);
        Assert.False(device.ConnectionRequested);
        Assert.Equal(SerialConnectionState.Disconnected, device.ConnectionState);
        Assert.False(device.Ready);

        openGate.Set();
        Assert.False(await connect.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => blocked.DisposeCalls == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, blocked.CloseCalls);
        Assert.Equal(0, blocked.DataReceivedSubscriptions);
        Assert.Equal(0, blocked.PinChangedSubscriptions);
        Assert.Equal(SerialConnectionState.Disconnected, device.ConnectionState);
    }

    [Fact]
    public async Task Dispose_can_abandon_blocked_initial_open_without_waiting_for_transport_open()
    {
        var factory = new FakeSerialTransportFactory();
        var blocked = factory.Enqueue();
        using var openGate = new ManualResetEventSlim(false);
        blocked.OpenGate = openGate;
        var device = new SerialChannel(Settings(), factory);

        var connect = Task.Run(device.Connect);
        Assert.True(blocked.OpenEntered.Wait(TimeSpan.FromSeconds(2)));

        var dispose = Task.Run(device.Dispose);
        Assert.True(await dispose.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(SerialConnectionState.Disposed, device.ConnectionState);
        Assert.False(device.ConnectionRequested);

        openGate.Set();
        Assert.False(await connect.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => blocked.DisposeCalls == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, blocked.CloseCalls);
        Assert.Equal(SerialConnectionState.Disposed, device.ConnectionState);
    }

    [Fact]
    public async Task Disconnect_can_abandon_blocked_recovery_open_without_hanging()
    {
        var factory = new FakeSerialTransportFactory();
        var failed = factory.Enqueue();
        failed.OpenException = new IOException("missing");
        var blocked = factory.Enqueue();
        using var openGate = new ManualResetEventSlim(false);
        blocked.OpenGate = openGate;
        using var device = new SerialChannel(Settings(), factory) { RecoveryPolicy = FastRecovery() };

        Assert.False(device.Connect());
        Assert.True(blocked.OpenEntered.Wait(TimeSpan.FromSeconds(2)));

        var disconnect = Task.Run(device.Disconnect);
        Assert.True(await disconnect.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await disconnect);
        Assert.False(device.ConnectionRequested);
        Assert.Equal(SerialConnectionState.Disconnected, device.ConnectionState);

        openGate.Set();
        Assert.True(SpinWait.SpinUntil(() => blocked.DisposeCalls == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, blocked.CloseCalls);
        Assert.Equal(0, blocked.DataReceivedSubscriptions);
        Assert.Equal(0, blocked.PinChangedSubscriptions);
        Assert.Equal(SerialConnectionState.Disconnected, device.ConnectionState);
        Assert.Equal(0, device.Telemetry.SuccessfulRecoveries);
    }

    [Fact]
    public async Task Late_completion_of_abandoned_open_cannot_contaminate_new_connection()
    {
        var factory = new FakeSerialTransportFactory();
        var blocked = factory.Enqueue();
        var replacement = factory.Enqueue();
        using var openGate = new ManualResetEventSlim(false);
        blocked.OpenGate = openGate;
        using var device = new SerialChannel(Settings(), factory);

        var firstConnect = Task.Run(device.Connect);
        Assert.True(blocked.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(await Task.Run(device.Disconnect).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await firstConnect.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.True(device.Connect());
        Assert.True(device.Ready);
        Assert.True(device.Command(new byte[] { 0x22 }));
        Assert.True(replacement.WriteExited.Wait(TimeSpan.FromSeconds(2)));

        openGate.Set();
        Assert.True(SpinWait.SpinUntil(() => blocked.DisposeCalls == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, blocked.CloseCalls);
        Assert.Equal(1, replacement.WriteCalls);
        Assert.Equal(SerialConnectionState.Connected, device.ConnectionState);
        Assert.True(device.Ready);
    }
}

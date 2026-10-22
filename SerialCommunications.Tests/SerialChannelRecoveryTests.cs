using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class SerialChannelRecoveryTests
{
    private static SerialPortSettings Settings() => new("TEST", 115200);
    private static SerialRecoveryPolicy FastRecovery(int maximumAttempts = -1) => new(TimeSpan.FromMilliseconds(20), maximumAttempts);

    [Fact]
    public void Failed_initial_connect_is_retried_with_a_fresh_transport()
    {
        var factory = new FakeSerialTransportFactory();
        var failed = factory.Enqueue(); failed.OpenException = new IOException("missing");
        var replacement = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory) { RecoveryPolicy = FastRecovery() };
        Assert.False(device.Connect());
        Assert.True(SpinWait.SpinUntil(() => device.ConnectionState == SerialConnectionState.Connected, TimeSpan.FromSeconds(2)));
        Assert.True(device.ConnectionRequested); Assert.True(device.Ready); Assert.False(device.RecoveryActive);
        Assert.Equal(1, device.RecoveryAttempts); Assert.Equal(1, device.Telemetry.RecoveryAttempts); Assert.Equal(1, device.Telemetry.SuccessfulRecoveries);
        Assert.Equal(2, factory.Created.Count); Assert.Equal(1, failed.CloseCalls); Assert.Equal(1, failed.DisposeCalls); Assert.True(replacement.IsOpen);
    }

    [Fact]
    public void Disappearing_endpoint_releases_old_transport_and_reacquires_fresh_transport()
    {
        var factory = new FakeSerialTransportFactory(); var original = factory.Enqueue(); var replacement = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory) { RecoveryPolicy = FastRecovery() };
        var disconnectingEvents = 0; device.Disconnecting += (_, _) => disconnectingEvents++; Assert.True(device.Connect());
        original.IsAvailable = false;
        Assert.True(SpinWait.SpinUntil(() => factory.Created.Count >= 2 && device.ConnectionState == SerialConnectionState.Connected, TimeSpan.FromSeconds(2)));
        Assert.True(device.ConnectionRequested); Assert.Equal(1, disconnectingEvents); Assert.Equal(1, original.CloseCalls); Assert.Equal(1, original.DisposeCalls);
        Assert.Equal(0, original.DataReceivedSubscriptions); Assert.Equal(0, original.PinChangedSubscriptions); Assert.True(replacement.IsOpen);
        Assert.Equal(1, device.Telemetry.SuccessfulRecoveries);
    }

    [Fact]
    public void Explicit_disconnect_cancels_pending_recovery()
    {
        var factory = new FakeSerialTransportFactory(); var failed = factory.Enqueue(); failed.OpenException = new IOException("missing"); factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory) { RecoveryPolicy = new SerialRecoveryPolicy(TimeSpan.FromMilliseconds(250)) };
        Assert.False(device.Connect()); Assert.True(device.Disconnect()); Thread.Sleep(350);
        Assert.False(device.ConnectionRequested); Assert.False(device.RecoveryActive); Assert.Equal(SerialConnectionState.Disconnected, device.ConnectionState); Assert.Single(factory.Created);
    }

    [Fact]
    public void Dispose_cancels_pending_recovery()
    {
        var factory = new FakeSerialTransportFactory(); var failed = factory.Enqueue(); failed.OpenException = new IOException("missing"); factory.Enqueue();
        var device = new SerialChannel(Settings(), factory) { RecoveryPolicy = new SerialRecoveryPolicy(TimeSpan.FromMilliseconds(250)) };
        Assert.False(device.Connect()); device.Dispose(); Thread.Sleep(350);
        Assert.Equal(SerialConnectionState.Disposed, device.ConnectionState); Assert.False(device.ConnectionRequested); Assert.False(device.RecoveryActive); Assert.Single(factory.Created);
    }

    [Fact]
    public async Task Disconnect_wins_cleanly_when_recovery_success_is_already_in_flight()
    {
        var factory = new FakeSerialTransportFactory(); var failed = factory.Enqueue(); failed.OpenException = new IOException("missing"); var replacement = factory.Enqueue();
        using var openGate = new ManualResetEventSlim(false); replacement.OpenGate = openGate;
        using var device = new SerialChannel(Settings(), factory) { RecoveryPolicy = FastRecovery() };
        Assert.False(device.Connect()); Assert.True(replacement.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
        var disconnect = Task.Run(device.Disconnect); Assert.False(disconnect.Wait(TimeSpan.FromMilliseconds(50))); openGate.Set(); Assert.True(await disconnect.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(device.ConnectionRequested); Assert.Equal(SerialConnectionState.Disconnected, device.ConnectionState); Assert.Equal(1, replacement.CloseCalls); Assert.Equal(1, replacement.DisposeCalls);
        Assert.Equal(0, replacement.DataReceivedSubscriptions); Assert.Equal(0, replacement.PinChangedSubscriptions);
    }

    [Fact]
    public void Recovery_stops_at_attempt_limit_without_clearing_connection_intent()
    {
        var factory = new FakeSerialTransportFactory();
        for (var i = 0; i < 3; i++) { var transport = factory.Enqueue(); transport.OpenException = new IOException("missing"); }
        using var device = new SerialChannel(Settings(), factory) { RecoveryPolicy = FastRecovery(maximumAttempts: 2) }; Assert.False(device.Connect());
        Assert.True(SpinWait.SpinUntil(() => device.RecoveryAttempts == 2 && device.ConnectionState == SerialConnectionState.Unavailable && !device.RecoveryActive, TimeSpan.FromSeconds(2)));
        Assert.True(device.ConnectionRequested); Assert.Equal(2, device.Telemetry.RecoveryAttempts); Assert.Equal(3, factory.Created.Count);
        Assert.All(factory.Created, transport => Assert.Equal(1, transport.DisposeCalls));
    }

    [Fact]
    public void Isolated_io_failure_remains_communications_evidence_not_transport_loss()
    {
        var factory = new FakeSerialTransportFactory(); var transport = factory.Enqueue(); transport.WriteException = new IOException("isolated write failure");
        using var device = new SerialChannel(Settings(), factory) { RecoveryPolicy = FastRecovery() }; Assert.True(device.Connect());
        Assert.True(device.Command(new byte[] { 0x41 })); Assert.True(transport.WriteEntered.Wait(TimeSpan.FromSeconds(2))); Thread.Sleep(400);
        Assert.True(device.ConnectionRequested); Assert.Equal(SerialConnectionState.Connected, device.ConnectionState); Assert.True(device.Ready); Assert.Single(factory.Created); Assert.Equal(0, device.RecoveryAttempts);
    }
}

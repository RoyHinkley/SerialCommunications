using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class SerialChannelAcquisitionEdgeCaseTests
{
    private static SerialPortSettings Settings(string portName = "TEST", int baudRate = 115200) => new(portName, baudRate);
    private static SerialRecoveryPolicy FastRecovery() => new(TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task Concurrent_connect_calls_share_one_in_flight_acquisition()
    {
        var factory = new FakeSerialTransportFactory();
        var blocked = factory.Enqueue();
        factory.Enqueue();
        using var gate = new ManualResetEventSlim(false);
        blocked.OpenGate = gate;
        using var device = new SerialChannel(Settings(), factory);

        var first = Task.Run(device.Connect);
        Assert.True(blocked.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
        var second = Task.Run(device.Connect);
        Thread.Sleep(50);

        Assert.Single(factory.Created);
        gate.Set();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Single(factory.Created);
        Assert.Equal(SerialConnectionState.Connected, device.ConnectionState);
    }

    [Fact]
    public async Task Disconnect_releases_all_callers_waiting_on_same_acquisition()
    {
        var factory = new FakeSerialTransportFactory();
        var blocked = factory.Enqueue();
        using var gate = new ManualResetEventSlim(false);
        blocked.OpenGate = gate;
        using var device = new SerialChannel(Settings(), factory);

        var first = Task.Run(device.Connect);
        Assert.True(blocked.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
        var second = Task.Run(device.Connect);
        Thread.Sleep(50);

        Assert.True(await Task.Run(device.Disconnect).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(SerialConnectionState.Disconnected, device.ConnectionState);

        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => blocked.DisposeCalls == 1, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Dispose_can_abandon_blocked_recovery_open_without_hanging()
    {
        var factory = new FakeSerialTransportFactory();
        var failed = factory.Enqueue();
        failed.OpenException = new IOException("missing");
        var blocked = factory.Enqueue();
        using var gate = new ManualResetEventSlim(false);
        blocked.OpenGate = gate;
        var device = new SerialChannel(Settings(), factory) { RecoveryPolicy = FastRecovery() };

        Assert.False(device.Connect());
        Assert.True(blocked.OpenEntered.Wait(TimeSpan.FromSeconds(2)));

        await Task.Run(device.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(SerialConnectionState.Disposed, device.ConnectionState);
        Assert.False(device.ConnectionRequested);
        Assert.Equal(0, device.Telemetry.SuccessfulRecoveries);

        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => blocked.DisposeCalls == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(0, blocked.DataReceivedSubscriptions);
        Assert.Equal(0, blocked.PinChangedSubscriptions);
        Assert.Equal(SerialConnectionState.Disposed, device.ConnectionState);
    }

    [Fact]
    public async Task Command_while_connecting_is_rejected_and_not_staged_for_session()
    {
        var factory = new FakeSerialTransportFactory();
        var blocked = factory.Enqueue();
        using var gate = new ManualResetEventSlim(false);
        blocked.OpenGate = gate;
        using var device = new SerialChannel(Settings(), factory);

        var connect = Task.Run(device.Connect);
        Assert.True(blocked.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(SerialConnectionState.Connecting, device.ConnectionState);

        Assert.False(device.Command(new byte[] { 0x41 }));
        Assert.False(device.HaveWork);

        gate.Set();
        Assert.True(await connect.WaitAsync(TimeSpan.FromSeconds(2)));
        Thread.Sleep(25);
        Assert.Equal(0, blocked.WriteCalls);
        Assert.False(device.HaveWork);
    }

    [Fact]
    public async Task Protocol_change_while_connecting_applies_to_published_session()
    {
        var factory = new FakeSerialTransportFactory();
        var blocked = factory.Enqueue();
        using var gate = new ManualResetEventSlim(false);
        blocked.OpenGate = gate;
        using var device = new SerialChannel(Settings(), factory);

        var connect = Task.Run(device.Connect);
        Assert.True(blocked.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(device.StageProtocolSettings(SerialProtocolSettings.Xor()));
        Assert.Equal(MessageCheckKind.Xor, device.ProtocolSettings.CheckKind);
        Assert.Null(device.PendingProtocolSettings);

        gate.Set();
        Assert.True(await connect.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(device.Command(new byte[] { 0x10, 0x20, 0x31 }));
        Assert.True(blocked.WriteExited.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(new byte[] { 0x10, 0x20, 0x31, 0x01 }, blocked.Writes.Single());
    }

    [Fact]
    public async Task Transport_configuration_change_during_open_does_not_mutate_in_flight_candidate()
    {
        var factory = new FakeSerialTransportFactory();
        var blocked = factory.Enqueue();
        using var gate = new ManualResetEventSlim(false);
        blocked.OpenGate = gate;
        using var device = new SerialChannel(Settings(), factory) { RtsMode = SerialChannel.RtsModes.Enabled };

        var connect = Task.Run(device.Connect);
        Assert.True(blocked.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
        device.PortSettings = Settings("NEXT", 57600);
        device.RtsMode = SerialChannel.RtsModes.Disabled;

        Assert.Equal("TEST", blocked.Settings!.PortName);
        Assert.Equal(115200, blocked.Settings.BaudRate);
        Assert.Equal(SerialChannel.RtsModes.Enabled, blocked.RtsMode);

        gate.Set();
        Assert.True(await connect.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(SerialConnectionState.Connected, device.ConnectionState);
    }

    [Fact]
    public async Task Late_failure_from_abandoned_open_cannot_change_new_session_state_or_report_connect_failure()
    {
        var factory = new FakeSerialTransportFactory();
        var abandoned = factory.Enqueue();
        abandoned.OpenException = new IOException("late failure");
        var replacement = factory.Enqueue();
        using var gate = new ManualResetEventSlim(false);
        abandoned.OpenGate = gate;
        using var device = new SerialChannel(Settings(), factory);
        var errors = new List<string>();
        var sync = new object();
        device.Error += message => { lock (sync) errors.Add(message); };

        var first = Task.Run(device.Connect);
        Assert.True(abandoned.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(await Task.Run(device.Disconnect).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(device.Connect());
        lock (sync) errors.Clear();

        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => abandoned.DisposeCalls == 1, TimeSpan.FromSeconds(2)));
        Thread.Sleep(25);

        Assert.Equal(SerialConnectionState.Connected, device.ConnectionState);
        Assert.True(device.Ready);
        Assert.True(replacement.IsOpen);
        lock (sync) Assert.DoesNotContain(errors, e => e.Contains("late failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cleanup_failure_from_abandoned_open_cannot_change_new_session_state()
    {
        var factory = new FakeSerialTransportFactory();
        var abandoned = factory.Enqueue();
        abandoned.CloseException = new IOException("late close failure");
        abandoned.DisposeException = new IOException("late dispose failure");
        var replacement = factory.Enqueue();
        using var gate = new ManualResetEventSlim(false);
        abandoned.OpenGate = gate;
        using var device = new SerialChannel(Settings(), factory);

        var first = Task.Run(device.Connect);
        Assert.True(abandoned.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(await Task.Run(device.Disconnect).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(device.Connect());

        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => abandoned.DisposeCalls == 1, TimeSpan.FromSeconds(2)));

        Assert.Equal(SerialConnectionState.Connected, device.ConnectionState);
        Assert.True(device.Ready);
        Assert.True(replacement.IsOpen);
    }

    [Fact]
    public async Task Abandoned_open_never_publishes_connected_event_or_signal_state()
    {
        var factory = new FakeSerialTransportFactory();
        var abandoned = factory.Enqueue();
        abandoned.RtsEnable = true;
        abandoned.CtsHolding = true;
        using var gate = new ManualResetEventSlim(false);
        abandoned.OpenGate = gate;
        using var device = new SerialChannel(Settings(), factory);
        var connected = 0;
        var signals = 0;
        device.Connected += (_, _) => Interlocked.Increment(ref connected);
        device.SignalsChanged += _ => Interlocked.Increment(ref signals);

        var connect = Task.Run(device.Connect);
        Assert.True(abandoned.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(await Task.Run(device.Disconnect).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await connect.WaitAsync(TimeSpan.FromSeconds(2)));

        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => abandoned.DisposeCalls == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(0, Volatile.Read(ref connected));
        Assert.Equal(0, Volatile.Read(ref signals));
    }

    [Fact]
    public async Task Repeated_disconnect_while_open_is_abandoned_remains_idempotent()
    {
        var factory = new FakeSerialTransportFactory();
        var abandoned = factory.Enqueue();
        using var gate = new ManualResetEventSlim(false);
        abandoned.OpenGate = gate;
        using var device = new SerialChannel(Settings(), factory);

        var connect = Task.Run(device.Connect);
        Assert.True(abandoned.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(await Task.Run(device.Disconnect).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(device.Disconnect());
        Assert.False(await connect.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(SerialConnectionState.Disconnected, device.ConnectionState);

        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => abandoned.DisposeCalls == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, abandoned.CloseCalls);
        Assert.Equal(1, abandoned.DisposeCalls);
    }

    [Fact]
    public async Task Dispose_after_disconnect_with_abandoned_open_prevents_late_publication()
    {
        var factory = new FakeSerialTransportFactory();
        var abandoned = factory.Enqueue();
        using var gate = new ManualResetEventSlim(false);
        abandoned.OpenGate = gate;
        var device = new SerialChannel(Settings(), factory);

        var connect = Task.Run(device.Connect);
        Assert.True(abandoned.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(await Task.Run(device.Disconnect).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await connect.WaitAsync(TimeSpan.FromSeconds(2)));
        device.Dispose();

        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => abandoned.DisposeCalls == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(SerialConnectionState.Disposed, device.ConnectionState);
        Assert.False(device.ConnectionRequested);
        Assert.False(device.Ready);
    }

    [Fact]
    public async Task Manual_connect_during_blocked_recovery_joins_existing_attempt_instead_of_starting_another()
    {
        var factory = new FakeSerialTransportFactory();
        var failed = factory.Enqueue();
        failed.OpenException = new IOException("missing");
        var recovery = factory.Enqueue();
        factory.Enqueue();
        using var gate = new ManualResetEventSlim(false);
        recovery.OpenGate = gate;
        using var device = new SerialChannel(Settings(), factory) { RecoveryPolicy = FastRecovery() };

        Assert.False(device.Connect());
        Assert.True(recovery.OpenEntered.Wait(TimeSpan.FromSeconds(2)));
        var manual = Task.Run(device.Connect);
        Thread.Sleep(50);

        Assert.Equal(2, factory.Created.Count);
        gate.Set();
        Assert.True(await manual.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(SerialConnectionState.Connected, device.ConnectionState);
        Assert.Equal(1, device.Telemetry.SuccessfulRecoveries);
    }
}

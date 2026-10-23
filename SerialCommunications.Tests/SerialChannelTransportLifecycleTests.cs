using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class SerialChannelTransportLifecycleTests
{
    private static SerialPortSettings Settings() => new("TEST", 115200);

    [Fact]
    public void Successful_connect_and_disconnect_track_state_intent_and_transport_ownership()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory);
        var disconnectingEvents = 0;
        device.Disconnecting += (_, _) => disconnectingEvents++;

        Assert.False(device.ConnectionRequested);
        Assert.Equal(SerialConnectionState.Disconnected, device.ConnectionState);

        Assert.True(device.Connect());
        Assert.True(device.ConnectionRequested);
        Assert.Equal(SerialConnectionState.Connected, device.ConnectionState);
        Assert.True(device.Ready);
        Assert.Equal("TEST", transport.Settings!.PortName);
        Assert.Equal(1, transport.DataReceivedSubscriptions);
        Assert.Equal(1, transport.PinChangedSubscriptions);

        Assert.True(device.Disconnect());
        Assert.False(device.ConnectionRequested);
        Assert.Equal(SerialConnectionState.Disconnected, device.ConnectionState);
        Assert.False(device.Ready);
        Assert.Equal(1, disconnectingEvents);
        Assert.Equal(0, transport.DataReceivedSubscriptions);
        Assert.Equal(0, transport.PinChangedSubscriptions);
        Assert.Equal(1, transport.CloseCalls);
        Assert.Equal(1, transport.DisposeCalls);
    }

    [Fact]
    public async Task Disconnect_closes_transport_to_interrupt_blocked_worker_before_joining_it()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var writeGate = new ManualResetEventSlim(false);
        transport.WriteGate = writeGate;
        using var device = new SerialChannel(Settings(), factory);
        Assert.True(device.Connect());
        Assert.True(device.Command(new byte[] { 0x41 }));
        Assert.True(transport.WriteEntered.Wait(TimeSpan.FromSeconds(2)));

        var disconnect = Task.Run(device.Disconnect);
        Assert.True(await disconnect.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(SerialConnectionState.Disconnected, device.ConnectionState);
        Assert.False(device.ConnectionRequested);
        Assert.Equal(1, transport.CloseCalls);
        Assert.Equal(1, transport.DisposeCalls);
        Assert.Equal(0, transport.DataReceivedSubscriptions);
        Assert.Equal(0, transport.PinChangedSubscriptions);
    }

    [Fact]
    public async Task Disconnect_disposes_transport_before_join_when_close_does_not_interrupt_blocked_worker()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var writeGate = new ManualResetEventSlim(false);
        transport.WriteGate = writeGate;
        transport.CloseInterruptsBlockedWrite = false;
        using var device = new SerialChannel(Settings(), factory);
        Assert.True(device.Connect());
        Assert.True(device.Command(new byte[] { 0x41 }));
        Assert.True(transport.WriteEntered.Wait(TimeSpan.FromSeconds(2)));

        var disconnect = Task.Run(device.Disconnect);
        Assert.True(await disconnect.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(SerialConnectionState.Disconnected, device.ConnectionState);
        Assert.False(device.ConnectionRequested);
        Assert.Equal(1, transport.CloseCalls);
        Assert.Equal(1, transport.DisposeCalls);
        Assert.Equal(0, transport.DataReceivedSubscriptions);
        Assert.Equal(0, transport.PinChangedSubscriptions);
    }

    [Fact]
    public void Failed_open_releases_transport_but_preserves_connection_intent()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        transport.OpenException = new IOException("open failed");
        using var device = new SerialChannel(Settings(), factory);
        var disconnectingEvents = 0;
        device.Disconnecting += (_, _) => disconnectingEvents++;

        Assert.False(device.Connect());

        Assert.True(device.ConnectionRequested);
        Assert.Equal(SerialConnectionState.Unavailable, device.ConnectionState);
        Assert.False(device.Ready);
        Assert.Equal(0, disconnectingEvents);
        Assert.Equal(1, transport.OpenCalls);
        Assert.Equal(1, transport.CloseCalls);
        Assert.Equal(1, transport.DisposeCalls);
        Assert.Equal(0, transport.DataReceivedSubscriptions);
        Assert.Equal(0, transport.PinChangedSubscriptions);
    }

    [Fact]
    public void Failure_after_open_releases_partial_transport_without_fictitious_disconnect()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        transport.DiscardInException = new IOException("discard failed");
        using var device = new SerialChannel(Settings(), factory);
        var disconnectingEvents = 0;
        device.Disconnecting += (_, _) => disconnectingEvents++;

        Assert.False(device.Connect());

        Assert.True(device.ConnectionRequested);
        Assert.Equal(SerialConnectionState.Unavailable, device.ConnectionState);
        Assert.False(device.Ready);
        Assert.Equal(0, disconnectingEvents);
        Assert.Equal(1, transport.OpenCalls);
        Assert.Equal(1, transport.CloseCalls);
        Assert.Equal(1, transport.DisposeCalls);
        Assert.Equal(0, transport.DataReceivedSubscriptions);
        Assert.Equal(0, transport.PinChangedSubscriptions);
    }

    [Fact]
    public void Cleanup_continues_when_close_and_dispose_throw()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        transport.DiscardInException = new IOException("initialization failed");
        transport.CloseException = new IOException("close failed");
        transport.DisposeException = new IOException("dispose failed");
        using var device = new SerialChannel(Settings(), factory);

        Assert.False(device.Connect());

        Assert.Equal(SerialConnectionState.Unavailable, device.ConnectionState);
        Assert.Equal(1, transport.CloseCalls);
        Assert.Equal(1, transport.DisposeCalls);
        Assert.Equal(0, transport.DataReceivedSubscriptions);
        Assert.Equal(0, transport.PinChangedSubscriptions);
        Assert.False(device.Ready);
    }

    [Fact]
    public void Repeated_disconnect_is_harmless_and_does_not_repeat_transport_cleanup()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory);
        var disconnectingEvents = 0;
        device.Disconnecting += (_, _) => disconnectingEvents++;
        Assert.True(device.Connect());

        Assert.True(device.Disconnect());
        Assert.True(device.Disconnect());

        Assert.Equal(SerialConnectionState.Disconnected, device.ConnectionState);
        Assert.False(device.ConnectionRequested);
        Assert.Equal(1, disconnectingEvents);
        Assert.Equal(1, transport.CloseCalls);
        Assert.Equal(1, transport.DisposeCalls);
    }

    [Fact]
    public void Disconnect_from_unavailable_clears_intent_without_recleaning_failed_transport()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        transport.OpenException = new IOException("open failed");
        using var device = new SerialChannel(Settings(), factory);
        Assert.False(device.Connect());

        Assert.True(device.Disconnect());

        Assert.False(device.ConnectionRequested);
        Assert.Equal(SerialConnectionState.Disconnected, device.ConnectionState);
        Assert.Equal(1, transport.CloseCalls);
        Assert.Equal(1, transport.DisposeCalls);
    }

    [Fact]
    public void Manual_connect_after_failed_acquisition_uses_a_fresh_transport()
    {
        var factory = new FakeSerialTransportFactory();
        var failed = factory.Enqueue();
        failed.OpenException = new IOException("missing");
        var replacement = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory);

        Assert.False(device.Connect());
        Assert.True(device.Connect());

        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(1, failed.DisposeCalls);
        Assert.Equal(SerialConnectionState.Connected, device.ConnectionState);
        Assert.True(device.ConnectionRequested);
        Assert.True(replacement.IsOpen);
    }

    [Fact]
    public void Dispose_is_idempotent_and_disconnect_after_disposal_is_harmless()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        var device = new SerialChannel(Settings(), factory);
        Assert.True(device.Connect());

        device.Dispose();
        device.Dispose();
        Assert.True(device.Disconnect());
        device.Close();

        Assert.Equal(SerialConnectionState.Disposed, device.ConnectionState);
        Assert.False(device.ConnectionRequested);
        Assert.False(device.Ready);
        Assert.Equal(1, transport.CloseCalls);
        Assert.Equal(1, transport.DisposeCalls);
    }

    [Fact]
    public void Ordinary_operations_after_disposal_fail_predictably()
    {
        var factory = new FakeSerialTransportFactory();
        var device = new SerialChannel(Settings(), factory);
        device.Dispose();

        Assert.Throws<ObjectDisposedException>(() => device.Connect());
        Assert.Throws<ObjectDisposedException>(() => device.Command(new byte[] { 0x41 }));
        Assert.Throws<ObjectDisposedException>(() => device.StageProtocolSettings(device.ProtocolSettings));
        Assert.Throws<ObjectDisposedException>(() => device.WaitForIdle());
        Assert.True(device.Disconnect());
    }
}
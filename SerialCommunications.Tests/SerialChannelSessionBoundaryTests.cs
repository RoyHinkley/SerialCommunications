using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class SerialChannelSessionBoundaryTests
{
    private static SerialPortSettings Settings(string portName = "TEST", int baudRate = 115200) => new(portName, baudRate);
    private static SerialRecoveryPolicy DelayedRecovery() => new(TimeSpan.FromMilliseconds(150));

    [Fact]
    public void Recovery_after_failed_open_uses_latest_transport_configuration()
    {
        var factory = new FakeSerialTransportFactory();
        var failed = factory.Enqueue();
        failed.OpenException = new IOException("missing");
        var replacement = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory)
        {
            RecoveryPolicy = DelayedRecovery(),
            RtsMode = SerialChannel.RtsModes.Enabled
        };

        Assert.False(device.Connect());
        device.PortSettings = Settings("REPLACEMENT", 57600);
        device.RtsMode = SerialChannel.RtsModes.Disabled;

        Assert.True(SpinWait.SpinUntil(
            () => device.ConnectionState == SerialConnectionState.Connected,
            TimeSpan.FromSeconds(2)));

        Assert.Equal("TEST", failed.Settings!.PortName);
        Assert.Equal(115200, failed.Settings.BaudRate);
        Assert.Equal(SerialChannel.RtsModes.Enabled, failed.RtsMode);
        Assert.Equal("REPLACEMENT", replacement.Settings!.PortName);
        Assert.Equal(57600, replacement.Settings.BaudRate);
        Assert.Equal(SerialChannel.RtsModes.Disabled, replacement.RtsMode);
    }

    [Fact]
    public void Recovery_after_transport_loss_uses_latest_transport_configuration()
    {
        var factory = new FakeSerialTransportFactory();
        var original = factory.Enqueue();
        var replacement = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory)
        {
            RecoveryPolicy = new SerialRecoveryPolicy(TimeSpan.FromMilliseconds(20)),
            RtsMode = SerialChannel.RtsModes.Enabled
        };
        Assert.True(device.Connect());

        device.PortSettings = Settings("REPLACEMENT", 38400);
        device.RtsMode = SerialChannel.RtsModes.Disabled;
        original.IsAvailable = false;

        Assert.True(SpinWait.SpinUntil(
            () => factory.Created.Count >= 2 && device.ConnectionState == SerialConnectionState.Connected,
            TimeSpan.FromSeconds(2)));

        Assert.Equal("TEST", original.Settings!.PortName);
        Assert.Equal(115200, original.Settings.BaudRate);
        Assert.Equal(SerialChannel.RtsModes.Enabled, original.RtsMode);
        Assert.Equal("REPLACEMENT", replacement.Settings!.PortName);
        Assert.Equal(38400, replacement.Settings.BaudRate);
        Assert.Equal(SerialChannel.RtsModes.Disabled, replacement.RtsMode);
    }

    [Fact]
    public void Protocol_change_while_unavailable_applies_to_recovered_session()
    {
        var factory = new FakeSerialTransportFactory();
        var failed = factory.Enqueue();
        failed.OpenException = new IOException("missing");
        var replacement = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory) { RecoveryPolicy = DelayedRecovery() };

        Assert.False(device.Connect());
        Assert.True(device.StageProtocolSettings(SerialProtocolSettings.Xor()));
        Assert.Equal(MessageCheckKind.Xor, device.ProtocolSettings.CheckKind);
        Assert.Null(device.PendingProtocolSettings);

        Assert.True(SpinWait.SpinUntil(
            () => device.ConnectionState == SerialConnectionState.Connected,
            TimeSpan.FromSeconds(2)));
        Assert.True(device.Command(new byte[] { 0x10, 0x20, 0x31 }));
        Assert.True(replacement.WriteExited.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(new byte[] { 0x10, 0x20, 0x31, 0x01 }, replacement.Writes.Single());
    }

    [Fact]
    public void Pending_protocol_transition_from_lost_session_does_not_cross_into_recovered_session()
    {
        var factory = new FakeSerialTransportFactory();
        var original = factory.Enqueue();
        var replacement = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory)
        {
            RecoveryPolicy = new SerialRecoveryPolicy(TimeSpan.FromMilliseconds(20)),
            MaximumMillisecondsSilenceInMessage = 500
        };
        Assert.True(device.Connect());

        original.EnqueueReceived(0x11, 0x12);
        original.RaiseDataReceived();
        Assert.True(SpinWait.SpinUntil(() => device.Telemetry.BytesReceived >= 2, TimeSpan.FromSeconds(2)));
        Assert.True(device.StageProtocolSettings(SerialProtocolSettings.Xor()));
        Assert.NotNull(device.PendingProtocolSettings);
        Assert.Equal(MessageCheckKind.None, device.ProtocolSettings.CheckKind);

        original.IsAvailable = false;
        Assert.True(SpinWait.SpinUntil(
            () => factory.Created.Count >= 2 && device.ConnectionState == SerialConnectionState.Connected,
            TimeSpan.FromSeconds(2)));

        Assert.Null(device.PendingProtocolSettings);
        Assert.Equal(MessageCheckKind.None, device.ProtocolSettings.CheckKind);
        Assert.True(device.Command(new byte[] { 0x21, 0x22 }));
        Assert.True(replacement.WriteExited.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(new byte[] { 0x21, 0x22 }, replacement.Writes.Single());
    }

    [Fact]
    public void Explicit_disconnect_discards_pending_protocol_transition_from_old_session()
    {
        var factory = new FakeSerialTransportFactory();
        var original = factory.Enqueue();
        var replacement = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory) { MaximumMillisecondsSilenceInMessage = 500 };
        Assert.True(device.Connect());

        original.EnqueueReceived(0x31, 0x32);
        original.RaiseDataReceived();
        Assert.True(SpinWait.SpinUntil(() => device.Telemetry.BytesReceived >= 2, TimeSpan.FromSeconds(2)));
        Assert.True(device.StageProtocolSettings(SerialProtocolSettings.Xor()));
        Assert.NotNull(device.PendingProtocolSettings);

        Assert.True(device.Disconnect());
        Assert.Null(device.PendingProtocolSettings);
        Assert.Equal(MessageCheckKind.None, device.ProtocolSettings.CheckKind);

        Assert.True(device.Connect());
        Assert.True(device.Command(new byte[] { 0x41, 0x42 }));
        Assert.True(replacement.WriteExited.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(new byte[] { 0x41, 0x42 }, replacement.Writes.Single());
    }

    [Fact]
    public void Dispose_discards_pending_protocol_transition_from_old_session()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        var device = new SerialChannel(Settings(), factory) { MaximumMillisecondsSilenceInMessage = 500 };
        Assert.True(device.Connect());

        transport.EnqueueReceived(0x51, 0x52);
        transport.RaiseDataReceived();
        Assert.True(SpinWait.SpinUntil(() => device.Telemetry.BytesReceived >= 2, TimeSpan.FromSeconds(2)));
        Assert.True(device.StageProtocolSettings(SerialProtocolSettings.Xor()));
        Assert.NotNull(device.PendingProtocolSettings);

        device.Dispose();

        Assert.Null(device.PendingProtocolSettings);
        Assert.Equal(MessageCheckKind.None, device.ProtocolSettings.CheckKind);
        Assert.Equal(SerialConnectionState.Disposed, device.ConnectionState);
    }

    [Fact]
    public void Recovery_after_partial_acquisition_failure_uses_fresh_transport_and_session()
    {
        var factory = new FakeSerialTransportFactory();
        var failed = factory.Enqueue();
        failed.DiscardInException = new IOException("discard failed");
        var replacement = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory)
        {
            RecoveryPolicy = new SerialRecoveryPolicy(TimeSpan.FromMilliseconds(20))
        };

        Assert.False(device.Connect());
        Assert.Equal(1, failed.OpenCalls);
        Assert.Equal(1, failed.CloseCalls);
        Assert.Equal(1, failed.DisposeCalls);
        Assert.Equal(0, failed.DataReceivedSubscriptions);
        Assert.Equal(0, failed.PinChangedSubscriptions);

        Assert.True(SpinWait.SpinUntil(
            () => factory.Created.Count >= 2 && device.ConnectionState == SerialConnectionState.Connected,
            TimeSpan.FromSeconds(2)));

        Assert.True(replacement.IsOpen);
        Assert.Equal(1, replacement.DataReceivedSubscriptions);
        Assert.Equal(1, replacement.PinChangedSubscriptions);
        Assert.True(device.Ready);
        Assert.True(device.Command(new byte[] { 0x61 }));
        Assert.True(replacement.WriteExited.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(new byte[] { 0x61 }, replacement.Writes.Single());
    }

    [Fact]
    public void Recovery_continues_after_old_transport_cleanup_throws()
    {
        var factory = new FakeSerialTransportFactory();
        var original = factory.Enqueue();
        original.CloseException = new IOException("close failed");
        original.DisposeException = new IOException("dispose failed");
        var replacement = factory.Enqueue();
        using var device = new SerialChannel(Settings(), factory)
        {
            RecoveryPolicy = new SerialRecoveryPolicy(TimeSpan.FromMilliseconds(20))
        };
        Assert.True(device.Connect());

        original.IsAvailable = false;

        Assert.True(SpinWait.SpinUntil(
            () => factory.Created.Count >= 2 && device.ConnectionState == SerialConnectionState.Connected,
            TimeSpan.FromSeconds(2)));
        Assert.Equal(1, original.CloseCalls);
        Assert.Equal(1, original.DisposeCalls);
        Assert.Equal(0, original.DataReceivedSubscriptions);
        Assert.Equal(0, original.PinChangedSubscriptions);
        Assert.True(replacement.IsOpen);
        Assert.True(device.Ready);
    }
}

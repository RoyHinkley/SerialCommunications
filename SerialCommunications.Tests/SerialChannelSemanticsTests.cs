using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class SerialChannelSemanticsTests
{
    [Fact]
    public void Command_while_disconnected_is_rejected_without_being_staged_for_next_session()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var channel = new SerialChannel(new SerialPortSettings("TEST", 115200), factory);

        Assert.False(channel.Command(new byte[] { 0x41 }));
        Assert.False(channel.HaveWork);

        Assert.True(channel.Connect());
        Thread.Sleep(25);

        Assert.Equal(0, transport.WriteCalls);
        Assert.False(channel.HaveWork);
    }

    [Fact]
    public void Command_while_unavailable_is_rejected_without_being_staged_for_recovery_session()
    {
        var factory = new FakeSerialTransportFactory();
        var failed = factory.Enqueue();
        failed.OpenException = new IOException("missing");
        var replacement = factory.Enqueue();
        using var channel = new SerialChannel(new SerialPortSettings("TEST", 115200), factory)
        {
            RecoveryPolicy = new SerialRecoveryPolicy(TimeSpan.FromMilliseconds(20))
        };

        Assert.False(channel.Connect());
        Assert.Equal(SerialConnectionState.Unavailable, channel.ConnectionState);
        Assert.False(channel.Command(new byte[] { 0x41 }));
        Assert.False(channel.HaveWork);

        Assert.True(SpinWait.SpinUntil(
            () => channel.ConnectionState == SerialConnectionState.Connected,
            TimeSpan.FromSeconds(2)));
        Thread.Sleep(25);

        Assert.Equal(0, replacement.WriteCalls);
        Assert.False(channel.HaveWork);
    }

    [Fact]
    public void Suppressed_invalid_message_still_counts_as_received()
    {
        var options = TestCrc();
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var channel = new SerialChannel(new SerialChannelConfiguration(new SerialPortSettings("TEST", 115200))
        {
            ProtocolSettings = SerialProtocolSettings.Crc16(options, suppressInvalidMessages: true),
            MaximumMillisecondsSilenceInMessage = 5
        }, factory);
        var delivered = 0;
        channel.DataReceived += _ => Interlocked.Increment(ref delivered);
        Assert.True(channel.Connect());

        var wire = new Crc(options).Append(new byte[] { 0x10, 0x20, 0x30 });
        wire[^1] ^= 0x01;
        transport.EnqueueReceived(wire);
        transport.RaiseDataReceived();

        Assert.True(SpinWait.SpinUntil(() => channel.Telemetry.CheckFailures == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, channel.Telemetry.MessagesReceived);
        Assert.Equal(0, Volatile.Read(ref delivered));
    }

    [Fact]
    public void Crc_check_is_applied_to_transmitted_message()
    {
        var options = TestCrc();
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var channel = new SerialChannel(new SerialChannelConfiguration(new SerialPortSettings("TEST", 115200))
        {
            ProtocolSettings = SerialProtocolSettings.Crc16(options)
        }, factory);
        var traces = new List<string>();
        var sync = new object();
        channel.Trace += (message, _) => { lock (sync) traces.Add(message); };
        Assert.True(channel.Connect());

        var payload = new byte[] { 0x10, 0x20, 0x31 };
        var expected = new Crc(options).Append(payload);
        Assert.True(channel.Command(payload));

        Assert.True(SpinWait.SpinUntil(() => channel.Telemetry.MessagesTransmitted == 1, TimeSpan.FromSeconds(2)));
        Assert.Equal(expected, transport.Writes.Single());
        lock (sync) Assert.Contains(traces, entry => entry.Contains($"Transmit: {BinaryDataFormatter.ToHexBytes(expected)}", StringComparison.Ordinal));
    }

    [Fact]
    public void Telemetry_reset_clears_measurements_without_affecting_channel_operation()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var channel = new SerialChannel(new SerialPortSettings("TEST", 115200), factory);
        Assert.True(channel.Connect());

        Assert.True(channel.Command(new byte[] { 0x41 }));
        transport.EnqueueReceived(0x42);
        transport.RaiseDataReceived();
        Assert.True(SpinWait.SpinUntil(() =>
            channel.Telemetry.MessagesTransmitted == 1 && channel.Telemetry.MessagesReceived == 1,
            TimeSpan.FromSeconds(2)));
        Assert.True(channel.Telemetry.BytesReceived > 0);
        Assert.True(channel.Telemetry.ReceiveNotifications > 0);

        channel.Telemetry.Reset();

        Assert.Equal(0, channel.Telemetry.BytesReceived);
        Assert.Equal(0, channel.Telemetry.MessagesReceived);
        Assert.Equal(0, channel.Telemetry.MessagesTransmitted);
        Assert.Equal(0, channel.Telemetry.CheckFailures);
        Assert.Equal(0, channel.Telemetry.ReceiveNotifications);
        Assert.Equal(0, channel.Telemetry.RecoveryAttempts);
        Assert.Equal(0, channel.Telemetry.SuccessfulRecoveries);
        Assert.Equal(SerialConnectionState.Connected, channel.ConnectionState);
        Assert.True(channel.Ready);

        Assert.True(channel.Command(new byte[] { 0x43 }));
        Assert.True(SpinWait.SpinUntil(() => channel.Telemetry.MessagesTransmitted == 1, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Successful_transmission_is_counted_and_traced_after_write()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        using var channel = new SerialChannel(new SerialPortSettings("TEST", 115200), factory);
        var traces = new List<string>();
        var sync = new object();
        channel.Trace += (message, _) => { lock (sync) traces.Add(message); };
        Assert.True(channel.Connect());

        Assert.True(channel.Command(new byte[] { 0x41, 0x42 }));

        Assert.True(SpinWait.SpinUntil(() => channel.Telemetry.MessagesTransmitted == 1, TimeSpan.FromSeconds(2)));
        lock (sync) Assert.Contains(traces, entry => entry.Contains("Transmit: 41 42", StringComparison.Ordinal));
        Assert.Equal(1, transport.WriteCalls);
    }

    [Fact]
    public void Failed_write_is_neither_counted_nor_reported_as_transmitted()
    {
        var factory = new FakeSerialTransportFactory();
        var transport = factory.Enqueue();
        transport.WriteException = new IOException("write failed");
        using var channel = new SerialChannel(new SerialPortSettings("TEST", 115200), factory);
        var traces = new List<string>();
        var sync = new object();
        channel.Trace += (message, _) => { lock (sync) traces.Add(message); };
        Assert.True(channel.Connect());

        Assert.True(channel.Command(new byte[] { 0x41, 0x42 }));
        Assert.True(transport.WriteEntered.Wait(TimeSpan.FromSeconds(2)));
        Thread.Sleep(30);

        Assert.Equal(0, channel.Telemetry.MessagesTransmitted);
        lock (sync) Assert.DoesNotContain(traces, entry => entry.Contains("Transmit: 41 42", StringComparison.Ordinal));
    }

    [Fact]
    public void Subscriber_exception_is_diagnostic_detail_not_channel_error()
    {
        var factory = new FakeSerialTransportFactory();
        factory.Enqueue();
        using var channel = new SerialChannel(new SerialPortSettings("TEST", 115200), factory);
        var errors = new List<string>();
        var traces = new List<(string Message, TraceFlags Flags)>();
        channel.Error += errors.Add;
        channel.Trace += (message, flags) => traces.Add((message, flags));
        channel.Connected += (_, _) => throw new InvalidOperationException("consumer bug");

        Assert.True(channel.Connect());

        Assert.Empty(errors);
        Assert.Contains(traces, entry =>
            entry.Flags.HasFlag(TraceFlags.Detail) &&
            entry.Message.Contains("Connected subscriber exception", StringComparison.Ordinal));
    }

    private static CrcOptions TestCrc() => new()
    {
        Polynomial = 0xA001,
        InitialValue = 0xFFFF,
        ExpectedResidue = 0,
        PostInvert = false,
        MsBitFirst = false,
        MsByteFirst = false
    };
}

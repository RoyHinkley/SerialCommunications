using System.Diagnostics;
using System.IO.Ports;
using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

/// <summary>
/// Opt-in integration tests for acquisition failure and recovery using a real serial endpoint that is
/// temporarily owned by another SerialPort. Unlike physical-loss tests, these require no operator action and
/// exercise the same SerialCommunications behavior unchanged on Windows and Linux.
/// </summary>
public sealed class SerialPortContentionIntegrationTests
{
    private const int BaudRate = 9600;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Connect_while_port_is_owned_reports_unavailable_and_preserves_intent()
    {
        var hardware = RequireHardware();
        using var owner = OpenPort(hardware.DutPort);
        using var dut = CreateDut(hardware.DutPort);

        Assert.False(dut.Connect());
        Assert.True(dut.ConnectionRequested);
        Assert.Equal(SerialConnectionState.Unavailable, dut.ConnectionState);
        Assert.False(dut.Ready);
    }

    [Fact]
    public async Task Releasing_owned_port_allows_automatic_recovery_to_fresh_usable_transport()
    {
        var hardware = RequireHardware();
        using var peer = OpenPort(hardware.PeerPort);
        var owner = OpenPort(hardware.DutPort);
        using var dut = CreateDut(hardware.DutPort);

        Assert.False(dut.Connect());
        Assert.Equal(SerialConnectionState.Unavailable, dut.ConnectionState);
        Assert.True(dut.ConnectionRequested);

        owner.Dispose();
        await WaitForState(dut, SerialConnectionState.Connected, RecoveryTimeout);
        Assert.True(dut.Ready);
        Assert.True(dut.ConnectionRequested);
        Assert.True(dut.Telemetry.RecoveryAttempts > 0);
        Assert.True(dut.Telemetry.SuccessfulRecoveries > 0);

        var payload = Sequence(0x51, 32);
        Assert.True(dut.Command(payload));
        Assert.Equal(payload, ReadExactly(peer, payload.Length));
    }

    [Fact]
    public async Task Disconnect_while_port_is_owned_cancels_recovery_and_releases_future_acquisition()
    {
        var hardware = RequireHardware();
        var owner = OpenPort(hardware.DutPort);
        using var dut = CreateDut(hardware.DutPort);

        Assert.False(dut.Connect());
        Assert.Equal(SerialConnectionState.Unavailable, dut.ConnectionState);
        Assert.True(dut.ConnectionRequested);

        Assert.True(dut.Disconnect());
        Assert.Equal(SerialConnectionState.Disconnected, dut.ConnectionState);
        Assert.False(dut.ConnectionRequested);
        Assert.False(dut.RecoveryActive);

        owner.Dispose();
        await Task.Delay(TimeSpan.FromTicks(SerialRecoveryPolicy.Default.RetryInterval.Ticks * 2));
        Assert.Equal(SerialConnectionState.Disconnected, dut.ConnectionState);
        Assert.False(dut.ConnectionRequested);
        Assert.False(dut.Ready);

        AssertPortCanBeReopened(hardware.DutPort);
    }

    private static HardwarePair RequireHardware()
    {
        var dut = Environment.GetEnvironmentVariable("SERIAL_TEST_DUT_PORT");
        var peer = Environment.GetEnvironmentVariable("SERIAL_TEST_PEER_PORT");
        if (string.IsNullOrWhiteSpace(dut) || string.IsNullOrWhiteSpace(peer))
            Assert.Skip("Set SERIAL_TEST_DUT_PORT and SERIAL_TEST_PEER_PORT to run real serial transport integration tests.");
        return new HardwarePair(dut!, peer!);
    }

    private static SerialChannel CreateDut(string portName) => new(new SerialPortSettings(portName, BaudRate))
    {
        MaximumMillisecondsSilenceInMessage = 20,
        MillisecondsBetweenMessages = -1,
        MillisecondsBetweenBytes = -1,
        RecoveryPolicy = new SerialRecoveryPolicy(TimeSpan.FromMilliseconds(100))
    };

    private static SerialPort OpenPort(string portName)
    {
        var port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = (int)OperationTimeout.TotalMilliseconds,
            WriteTimeout = (int)OperationTimeout.TotalMilliseconds
        };
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        return port;
    }

    private static async Task WaitForState(SerialChannel dut, SerialConnectionState state, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (dut.ConnectionState == state) return;
            await Task.Delay(25);
        }
        Assert.Fail($"Timed out waiting for {state}; final state was {dut.ConnectionState}, intent={dut.ConnectionRequested}, ready={dut.Ready}.");
    }

    private static byte[] ReadExactly(SerialPort port, int count)
    {
        var result = new byte[count];
        var offset = 0;
        var sw = Stopwatch.StartNew();
        while (offset < count && sw.Elapsed < OperationTimeout)
        {
            try { offset += port.Read(result, offset, count - offset); }
            catch (TimeoutException) { break; }
        }
        Assert.Equal(count, offset);
        return result;
    }

    private static void AssertPortCanBeReopened(string portName)
    {
        using var probe = OpenPort(portName);
        Assert.True(probe.IsOpen);
    }

    private static byte[] Sequence(byte seed, int count)
    {
        var result = new byte[count];
        for (var i = 0; i < result.Length; i++) result[i] = unchecked((byte)(seed + i));
        return result;
    }

    private sealed record HardwarePair(string DutPort, string PeerPort);
}

using System.Diagnostics;
using System.IO.Ports;
using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

/// <summary>
/// Real-transport tests requiring modem-control wiring in addition to TX/RX/GND.
///
/// Wiring for the current cases: peer RTS -> DUT CTS, plus the ordinary TX/RX/GND cross-connect. The peer
/// deliberately controls DUT transmit permission so a SerialChannel write can be held in the OS/driver path.
/// The same tests are intended to run unchanged on Windows and Linux.
/// </summary>
public sealed class SerialPortFlowControlIntegrationTests
{
    private const int BaudRate = 9600;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    // This case was added when designing the blocked-write tests. SerialPortTransport currently initializes
    // RtsEnable even when Handshake.RequestToSend owns RTS. Some SerialPort implementations reject that
    // combination. Characterize it explicitly before relying on hardware flow control for teardown tests.
    [Fact]
    public void RequestToSend_handshake_connects_without_manual_RTS_conflict()
    {
        var hardware = RequireFlowControlHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        peer.RtsEnable = true;

        using var dut = CreateFlowControlledDut(hardware.DutPort);
        Assert.True(dut.Connect());
        Assert.Equal(SerialConnectionState.Connected, dut.ConnectionState);
        Assert.True(dut.Ready);
    }

    [Fact]
    public void Disconnect_while_hardware_flow_control_blocks_transmit_completes_within_bound()
    {
        var hardware = RequireFlowControlHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        peer.RtsEnable = false;

        using var dut = CreateFlowControlledDut(hardware.DutPort);
        Assert.True(dut.Connect());
        Assert.True(dut.Command(new byte[256 * 1024]));
        Assert.True(SpinWait.SpinUntil(() => dut.HaveWork, OperationTimeout));

        var sw = Stopwatch.StartNew();
        dut.Disconnect();
        sw.Stop();

        Assert.True(sw.Elapsed < OperationTimeout, $"Disconnect took {sw.Elapsed}.");
        Assert.False(dut.ConnectionRequested);
        Assert.Equal(SerialConnectionState.Disconnected, dut.ConnectionState);
    }

    [Fact]
    public void Dispose_while_hardware_flow_control_blocks_transmit_completes_within_bound()
    {
        var hardware = RequireFlowControlHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        peer.RtsEnable = false;

        var dut = CreateFlowControlledDut(hardware.DutPort);
        Assert.True(dut.Connect());
        Assert.True(dut.Command(new byte[256 * 1024]));
        Assert.True(SpinWait.SpinUntil(() => dut.HaveWork, OperationTimeout));

        var sw = Stopwatch.StartNew();
        dut.Dispose();
        sw.Stop();

        Assert.True(sw.Elapsed < OperationTimeout, $"Dispose took {sw.Elapsed}.");
    }

    private static HardwarePair RequireFlowControlHardware()
    {
        var dut = Environment.GetEnvironmentVariable("SERIAL_TEST_DUT_PORT");
        var peer = Environment.GetEnvironmentVariable("SERIAL_TEST_PEER_PORT");
        Assert.False(string.IsNullOrWhiteSpace(dut), "SERIAL_TEST_DUT_PORT is not configured.");
        Assert.False(string.IsNullOrWhiteSpace(peer), "SERIAL_TEST_PEER_PORT is not configured.");
        return new HardwarePair(dut!, peer!);
    }

    private static SerialChannel CreateFlowControlledDut(string portName) => new(new SerialPortSettings(
        portName,
        BaudRate,
        Parity.None,
        8,
        StopBits.One,
        Handshake.RequestToSend))
    {
        MaximumMillisecondsSilenceInMessage = 20,
        MillisecondsBetweenMessages = -1,
        MillisecondsBetweenBytes = -1
    };

    private static SerialPort OpenPeer(string portName)
    {
        var port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = (int)OperationTimeout.TotalMilliseconds,
            WriteTimeout = (int)OperationTimeout.TotalMilliseconds,
            RtsEnable = true
        };
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        return port;
    }

    private sealed record HardwarePair(string DutPort, string PeerPort);
}

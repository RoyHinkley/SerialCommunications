using System.Diagnostics;
using System.IO.Ports;
using System.Reflection;
using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

/// <summary>
/// Real-transport tests requiring modem-control wiring in addition to TX/RX/GND.
///
/// Wiring for the current cases: peer RTS -> DUT CTS, plus the ordinary TX/RX/GND cross-connect. The peer
/// deliberately controls DUT transmit permission so a SerialChannel write can be held in the OS/driver path.
/// The same tests are intended to run unchanged on Windows and Linux.
/// </summary>
[Collection(SerialHardwareCollection.Name)]
public sealed class SerialPortFlowControlIntegrationTests
{
    private const int BaudRate = 9600;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly FieldInfo? ChannelPortField =
        typeof(SerialChannel).GetField("port", BindingFlags.Instance | BindingFlags.NonPublic);

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
        WriteHardwareState("after connect", hardware, peer, dut);

        Assert.True(dut.Command(new byte[256 * 1024]));
        Assert.True(SpinWait.SpinUntil(() => dut.HaveWork, OperationTimeout));
        WriteHardwareState("before dispose", hardware, peer, dut);

        var sw = Stopwatch.StartNew();
        dut.Dispose();
        sw.Stop();

        WriteHardwareState("after dispose", hardware, peer, dut);
        Assert.True(sw.Elapsed < OperationTimeout, $"Dispose took {sw.Elapsed}.");
    }

    private static void WriteHardwareState(string stage, HardwarePair hardware, SerialPort peer, SerialChannel dut)
    {
        var ports = string.Join(",", SerialPort.GetPortNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        var peerState = $"{hardware.PeerPort}: open={peer.IsOpen}, RTS={peer.RtsEnable}, CTS={peer.CtsHolding}, DTR={peer.DtrEnable}, DSR={peer.DsrHolding}, CD={peer.CDHolding}";
        var dutState = ReadDutState(dut, hardware.DutPort);
        var channelState = $"channel: state={dut.ConnectionState}, requested={dut.ConnectionRequested}, ready={dut.Ready}, work={dut.HaveWork}";

        var message = $"*** SERIAL FLOW STATE [{stage}]: ports=[{ports}]; {peerState}; {dutState}; {channelState}";
        Debug.WriteLine(message);
        Console.WriteLine(message);
    }

    private static string ReadDutState(SerialChannel channel, string portName)
    {
        try
        {
            if (ChannelPortField?.GetValue(channel) is not ISerialTransport transport)
                return $"{portName}: no owned transport";

            string rts;
            try { rts = transport.RtsEnable.ToString(); }
            catch (Exception e) { rts = $"{e.GetType().Name}"; }

            return $"{portName}: open={transport.IsOpen}, RTS={rts}, CTS={transport.CtsHolding}, DTR={transport.DtrEnable}, DSR={transport.DsrHolding}, CD={transport.CDHolding}, bytesToRead={transport.BytesToRead}";
        }
        catch (Exception e)
        {
            return $"{portName}: state read {e.GetType().Name}: {e.Message}";
        }
    }

    private static HardwarePair RequireFlowControlHardware()
    {
        var enabled = Environment.GetEnvironmentVariable("SERIAL_TEST_RTS_CTS");
        if (!string.Equals(enabled, "1", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("Set SERIAL_TEST_RTS_CTS=1 to run real serial flow-control integration tests.");

        var dut = Environment.GetEnvironmentVariable("SERIAL_TEST_DUT_PORT");
        var peer = Environment.GetEnvironmentVariable("SERIAL_TEST_PEER_PORT");
        if (string.IsNullOrWhiteSpace(dut) || string.IsNullOrWhiteSpace(peer))
            Assert.Skip("Set SERIAL_TEST_DUT_PORT and SERIAL_TEST_PEER_PORT to run real serial flow-control integration tests.");
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

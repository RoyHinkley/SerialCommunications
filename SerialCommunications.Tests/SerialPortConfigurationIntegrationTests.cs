using System.Diagnostics;
using System.IO.Ports;
using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

/// <summary>
/// Opt-in real-transport tests for serial settings that exercise materially different OS/driver configuration
/// paths. These cases run unchanged on Windows and Linux and verify wire behavior rather than merely accepting
/// configuration properties.
/// </summary>
public sealed class SerialPortConfigurationIntegrationTests
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Seven_data_bits_even_parity_one_stop_bit_communicates_bidirectionally()
    {
        var hardware = RequireHardware();
        var settings = new SerialPortSettings(hardware.DutPort, 9600, Parity.Even, 7, StopBits.One);
        using var peer = OpenPeer(hardware.PeerPort, 9600, Parity.Even, 7, StopBits.One);
        using var dut = CreateDut(settings);

        Assert.True(dut.Connect());
        var fromDut = SevenBitSequence(0x21, 32);
        Assert.True(dut.Command(fromDut));
        Assert.Equal(fromDut, ReadExactly(peer, fromDut.Length));

        var fromPeer = SevenBitSequence(0x41, 32);
        var received = ReceiveNext(dut);
        peer.Write(fromPeer, 0, fromPeer.Length);
        Assert.Equal(fromPeer, received.WaitAsync(OperationTimeout).GetAwaiter().GetResult());
    }

    [Fact]
    public void Eight_data_bits_no_parity_two_stop_bits_communicates_bidirectionally()
    {
        var hardware = RequireHardware();
        var settings = new SerialPortSettings(hardware.DutPort, 9600, Parity.None, 8, StopBits.Two);
        using var peer = OpenPeer(hardware.PeerPort, 9600, Parity.None, 8, StopBits.Two);
        using var dut = CreateDut(settings);

        Assert.True(dut.Connect());
        var fromDut = Sequence(0x81, 32);
        Assert.True(dut.Command(fromDut));
        Assert.Equal(fromDut, ReadExactly(peer, fromDut.Length));

        var fromPeer = Sequence(0xB1, 32);
        var received = ReceiveNext(dut);
        peer.Write(fromPeer, 0, fromPeer.Length);
        Assert.Equal(fromPeer, received.WaitAsync(OperationTimeout).GetAwaiter().GetResult());
    }

    [Fact]
    public void Different_baud_rate_communicates_bidirectionally()
    {
        var hardware = RequireHardware();
        const int baudRate = 19200;
        var settings = new SerialPortSettings(hardware.DutPort, baudRate);
        using var peer = OpenPeer(hardware.PeerPort, baudRate, Parity.None, 8, StopBits.One);
        using var dut = CreateDut(settings);

        Assert.True(dut.Connect());
        var fromDut = Sequence(0x31, 48);
        Assert.True(dut.Command(fromDut));
        Assert.Equal(fromDut, ReadExactly(peer, fromDut.Length));

        var fromPeer = Sequence(0x91, 48);
        var received = ReceiveNext(dut);
        peer.Write(fromPeer, 0, fromPeer.Length);
        Assert.Equal(fromPeer, received.WaitAsync(OperationTimeout).GetAwaiter().GetResult());
    }

    [Fact]
    public void Port_settings_changed_between_sessions_apply_to_fresh_transport()
    {
        var hardware = RequireHardware();
        using var dut = CreateDut(new SerialPortSettings(hardware.DutPort, 9600));

        using (var peer = OpenPeer(hardware.PeerPort, 9600, Parity.None, 8, StopBits.One))
        {
            Assert.True(dut.Connect());
            var first = Sequence(0x51, 24);
            Assert.True(dut.Command(first));
            Assert.Equal(first, ReadExactly(peer, first.Length));
            Assert.True(SpinWait.SpinUntil(() => dut.Idle, OperationTimeout));
            Assert.True(dut.Disconnect());
        }

        dut.PortSettings = new SerialPortSettings(hardware.DutPort, 19200, Parity.Even, 7, StopBits.One);
        using var changedPeer = OpenPeer(hardware.PeerPort, 19200, Parity.Even, 7, StopBits.One);
        Assert.True(dut.Connect());

        var second = SevenBitSequence(0x61, 24);
        Assert.True(dut.Command(second));
        Assert.Equal(second, ReadExactly(changedPeer, second.Length));

        var response = SevenBitSequence(0x31, 24);
        var received = ReceiveNext(dut);
        changedPeer.Write(response, 0, response.Length);
        Assert.Equal(response, received.WaitAsync(OperationTimeout).GetAwaiter().GetResult());
    }

    private static HardwarePair RequireHardware()
    {
        var dut = Environment.GetEnvironmentVariable("SERIAL_TEST_DUT_PORT");
        var peer = Environment.GetEnvironmentVariable("SERIAL_TEST_PEER_PORT");
        if (string.IsNullOrWhiteSpace(dut) || string.IsNullOrWhiteSpace(peer))
            Assert.Skip("Set SERIAL_TEST_DUT_PORT and SERIAL_TEST_PEER_PORT to run real serial transport integration tests.");
        return new HardwarePair(dut!, peer!);
    }

    private static SerialChannel CreateDut(SerialPortSettings settings) => new(settings)
    {
        MaximumMillisecondsSilenceInMessage = 20,
        MillisecondsBetweenMessages = -1,
        MillisecondsBetweenBytes = -1
    };

    private static SerialPort OpenPeer(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits)
    {
        var port = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
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

    private static Task<byte[]> ReceiveNext(SerialChannel dut)
    {
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ReceivedData data)
        {
            dut.DataReceived -= Handler;
            received.TrySetResult(data.PayloadBytes.ToArray());
        }
        dut.DataReceived += Handler;
        return received.Task;
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

    private static byte[] Sequence(byte seed, int count)
    {
        var result = new byte[count];
        for (var i = 0; i < result.Length; i++) result[i] = unchecked((byte)(seed + i));
        return result;
    }

    private static byte[] SevenBitSequence(byte seed, int count)
    {
        var result = new byte[count];
        for (var i = 0; i < result.Length; i++) result[i] = (byte)((seed + i) & 0x7F);
        return result;
    }

    private sealed record HardwarePair(string DutPort, string PeerPort);
}

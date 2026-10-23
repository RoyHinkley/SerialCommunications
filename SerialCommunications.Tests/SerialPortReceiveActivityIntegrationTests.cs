using System.Diagnostics;
using System.IO.Ports;
using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

/// <summary>
/// Real-transport receive tests synchronized on bytes actually acquired by SerialChannel. This distinction is
/// important: a successful peer Write proves only that the peer/OS accepted bytes, not that the DUT receive
/// worker observed them.
/// </summary>
public sealed class SerialPortReceiveActivityIntegrationTests
{
    private const int BaudRate = 9600;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Continuous_receive_activity_is_observable_before_silence_delimited_message_completion()
    {
        var hardware = RequireHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateDut(hardware.DutPort);
        Assert.True(dut.Connect());

        using var traffic = StartPeerTraffic(peer);
        await WaitForBytesReceived(dut, minimumBytes: 128);

        Assert.True(dut.Telemetry.BytesReceived >= 128);
        Assert.True(dut.Telemetry.ReceiveNotifications > 0);
    }

    [Fact]
    public async Task Disconnect_after_confirmed_receive_activity_completes_within_bound_and_releases_port()
    {
        var hardware = RequireHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateDut(hardware.DutPort);
        Assert.True(dut.Connect());

        using var traffic = StartPeerTraffic(peer);
        await WaitForBytesReceived(dut, minimumBytes: 128);
        var bytesBeforeDisconnect = dut.Telemetry.BytesReceived;
        Assert.True(bytesBeforeDisconnect >= 128);

        var disconnect = Task.Run(dut.Disconnect);
        Assert.True(await disconnect.WaitAsync(OperationTimeout));
        Assert.Equal(SerialConnectionState.Disconnected, dut.ConnectionState);
        Assert.False(dut.ConnectionRequested);
        Assert.False(dut.Ready);

        AssertPortCanBeReopened(hardware.DutPort);
    }

    [Fact]
    public async Task Dispose_after_confirmed_receive_activity_completes_within_bound_and_releases_port()
    {
        var hardware = RequireHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        var dut = CreateDut(hardware.DutPort);
        Assert.True(dut.Connect());

        using var traffic = StartPeerTraffic(peer);
        await WaitForBytesReceived(dut, minimumBytes: 128);
        Assert.True(dut.Telemetry.BytesReceived >= 128);

        var dispose = Task.Run(dut.Dispose);
        await dispose.WaitAsync(OperationTimeout);
        Assert.Equal(SerialConnectionState.Disposed, dut.ConnectionState);
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
        MillisecondsBetweenBytes = -1
    };

    private static SerialPort OpenPeer(string portName)
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

    private static PeerTraffic StartPeerTraffic(SerialPort peer)
    {
        var cancellation = new CancellationTokenSource();
        var task = Task.Run(() =>
        {
            var block = Sequence(0x40, 64);
            while (!cancellation.IsCancellationRequested)
            {
                try { peer.Write(block, 0, block.Length); }
                catch (Exception) when (cancellation.IsCancellationRequested || !peer.IsOpen) { break; }
            }
        });
        return new PeerTraffic(cancellation, task);
    }

    private static async Task WaitForBytesReceived(SerialChannel dut, long minimumBytes)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < OperationTimeout)
        {
            if (dut.Telemetry.BytesReceived >= minimumBytes) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Timed out waiting for {minimumBytes} acquired bytes; observed {dut.Telemetry.BytesReceived} bytes and {dut.Telemetry.ReceiveNotifications} receive notifications.");
    }

    private static void AssertPortCanBeReopened(string portName)
    {
        using var probe = OpenPeer(portName);
        Assert.True(probe.IsOpen);
    }

    private static byte[] Sequence(byte seed, int count)
    {
        var result = new byte[count];
        for (var i = 0; i < result.Length; i++) result[i] = unchecked((byte)(seed + i));
        return result;
    }

    private sealed record HardwarePair(string DutPort, string PeerPort);

    private sealed class PeerTraffic(CancellationTokenSource cancellation, Task task) : IDisposable
    {
        public void Dispose()
        {
            cancellation.Cancel();
            try { task.Wait(OperationTimeout); }
            catch (AggregateException e) when (e.InnerExceptions.All(x => x is OperationCanceledException)) { }
            cancellation.Dispose();
        }
    }
}

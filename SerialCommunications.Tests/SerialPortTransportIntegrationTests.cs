using System.Diagnostics;
using System.IO.Ports;
using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

/// <summary>
/// Opt-in integration tests for the real System.IO.Ports transport.
///
/// These tests deliberately specify platform-independent SerialCommunications behavior. The same cases are
/// intended to run unchanged on Windows and Linux so implementation differences underneath SerialPort are
/// exposed as behavioral failures rather than hidden behind platform-specific expectations.
///
/// Hardware baseline: two serial endpoints with TX/RX/GND cross-connected. Set SERIAL_TEST_DUT_PORT and
/// SERIAL_TEST_PEER_PORT to enable the implemented cases. Physical-loss and RTS/CTS cases live in dedicated
/// fixtures because they require operator action or additional modem-control wiring.
/// </summary>
public sealed class SerialPortTransportIntegrationTests
{
    private const int BaudRate = 9600;
    private const int SlowBytePacingMilliseconds = 10;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Connect_communicate_disconnect_has_clean_lifecycle()
    {
        var hardware = RequireHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateDut(hardware.DutPort);

        Assert.True(dut.Connect());
        Assert.Equal(SerialConnectionState.Connected, dut.ConnectionState);
        Assert.True(dut.ConnectionRequested);
        Assert.True(dut.Ready);

        var payload = Sequence(0x11, 32);
        Assert.True(dut.Command(payload));
        Assert.Equal(payload, ReadExactly(peer, payload.Length));
        Assert.True(SpinWait.SpinUntil(() => dut.Idle, OperationTimeout));

        Assert.True(dut.Disconnect());
        Assert.Equal(SerialConnectionState.Disconnected, dut.ConnectionState);
        Assert.False(dut.ConnectionRequested);
        Assert.False(dut.Ready);

        AssertPortCanBeReopened(hardware.DutPort);
    }

    [Fact]
    public void Disconnect_then_reconnect_uses_a_clean_new_session()
    {
        var hardware = RequireHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateDut(hardware.DutPort);

        Assert.True(dut.Connect());
        var first = Sequence(0x21, 24);
        Assert.True(dut.Command(first));
        Assert.Equal(first, ReadExactly(peer, first.Length));
        Assert.True(SpinWait.SpinUntil(() => dut.Idle, OperationTimeout));
        Assert.True(dut.Disconnect());

        DrainPeer(peer);
        Assert.True(dut.Connect());
        var second = Sequence(0x61, 24);
        Assert.True(dut.Command(second));
        Assert.Equal(second, ReadExactly(peer, second.Length));
        Assert.True(SpinWait.SpinUntil(() => dut.Idle, OperationTimeout));
        Assert.Equal(SerialConnectionState.Connected, dut.ConnectionState);
        Assert.True(dut.Ready);
    }

    [Fact]
    public void Repeated_connect_communicate_disconnect_cycles_remain_clean()
    {
        var hardware = RequireHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateDut(hardware.DutPort);

        for (var cycle = 0; cycle < 20; cycle++)
        {
            Assert.True(dut.Connect());
            var payload = Sequence((byte)(0x30 + cycle), 16);
            Assert.True(dut.Command(payload));
            Assert.Equal(payload, ReadExactly(peer, payload.Length));
            Assert.True(SpinWait.SpinUntil(() => dut.Idle, OperationTimeout));
            Assert.True(dut.Disconnect());
            Assert.Equal(SerialConnectionState.Disconnected, dut.ConnectionState);
            Assert.False(dut.ConnectionRequested);
        }

        AssertPortCanBeReopened(hardware.DutPort);
    }

    [Fact]
    public void Dispose_then_recreate_on_same_port_remains_usable()
    {
        var hardware = RequireHardware();
        using var peer = OpenPeer(hardware.PeerPort);

        for (var cycle = 0; cycle < 10; cycle++)
        {
            using var dut = CreateDut(hardware.DutPort);
            Assert.True(dut.Connect());
            var payload = Sequence((byte)(0x80 + cycle), 16);
            Assert.True(dut.Command(payload));
            Assert.Equal(payload, ReadExactly(peer, payload.Length));
            Assert.True(SpinWait.SpinUntil(() => dut.Idle, OperationTimeout));
        }

        AssertPortCanBeReopened(hardware.DutPort);
    }

    [Fact]
    public async Task Bidirectional_traffic_is_delivered_without_loss_or_cross_session_data()
    {
        var hardware = RequireHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateDut(hardware.DutPort);
        Assert.True(dut.Connect());

        var fromDut = Sequence(0x41, 48);
        Assert.True(dut.Command(fromDut));
        Assert.Equal(fromDut, ReadExactly(peer, fromDut.Length));

        var fromPeer = Sequence(0xA1, 48);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        dut.DataReceived += data => received.TrySetResult(data.PayloadBytes.ToArray());
        peer.Write(fromPeer, 0, fromPeer.Length);

        Assert.Equal(fromPeer, await received.Task.WaitAsync(OperationTimeout));
    }

    [Fact]
    public async Task Disconnect_during_active_receive_completes_within_bound_and_releases_port()
    {
        var hardware = RequireHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateDut(hardware.DutPort);
        Assert.True(dut.Connect());

        using var traffic = StartPeerTraffic(peer);
        await traffic.Progress.Task.WaitAsync(OperationTimeout);

        await AssertCompletesWithin(() => dut.Disconnect());
        Assert.Equal(SerialConnectionState.Disconnected, dut.ConnectionState);
        Assert.False(dut.ConnectionRequested);
        Assert.False(dut.Ready);
        AssertPortCanBeReopened(hardware.DutPort);
    }

    [Fact]
    public async Task Dispose_during_active_receive_completes_within_bound_and_releases_port()
    {
        var hardware = RequireHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        var dut = CreateDut(hardware.DutPort);
        Assert.True(dut.Connect());

        using var traffic = StartPeerTraffic(peer);
        await traffic.Progress.Task.WaitAsync(OperationTimeout);

        await AssertCompletesWithin(dut.Dispose);
        AssertPortCanBeReopened(hardware.DutPort);
    }

    [Fact]
    public async Task Disconnect_during_active_transmit_completes_within_bound_and_releases_port()
    {
        var hardware = RequireHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateSlowDut(hardware.DutPort);
        Assert.True(dut.Connect());

        var payload = Sequence(0x31, 200);
        Assert.True(dut.Command(payload));
        var prefix = ReadExactly(peer, 8);
        Assert.Equal(payload[..prefix.Length], prefix);
        Assert.True(dut.HaveWork);

        await AssertCompletesWithin(() => dut.Disconnect());
        Assert.Equal(SerialConnectionState.Disconnected, dut.ConnectionState);
        Assert.False(dut.ConnectionRequested);
        AssertPortCanBeReopened(hardware.DutPort);
    }

    [Fact]
    public async Task Dispose_during_active_transmit_completes_within_bound_and_releases_port()
    {
        var hardware = RequireHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        var dut = CreateSlowDut(hardware.DutPort);
        Assert.True(dut.Connect());

        var payload = Sequence(0x71, 200);
        Assert.True(dut.Command(payload));
        var prefix = ReadExactly(peer, 8);
        Assert.Equal(payload[..prefix.Length], prefix);
        Assert.True(dut.HaveWork);

        await AssertCompletesWithin(dut.Dispose);
        AssertPortCanBeReopened(hardware.DutPort);
    }

    [Fact]
    public void Disconnect_with_queued_transmissions_does_not_leak_commands_into_next_session()
    {
        var hardware = RequireHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateSlowDut(hardware.DutPort);
        Assert.True(dut.Connect());

        var active = Sequence(0x11, 120);
        var queued = Sequence(0x91, 24);
        Assert.True(dut.Command(active));
        Assert.True(dut.Command(queued));
        var prefix = ReadExactly(peer, 8);
        Assert.Equal(active[..prefix.Length], prefix);
        Assert.True(dut.HaveWork);

        Assert.True(dut.Disconnect());
        DrainPeer(peer);

        Assert.True(dut.Connect());
        var nextSession = Sequence(0xD1, 24);
        Assert.True(dut.Command(nextSession));
        Assert.Equal(nextSession, ReadExactly(peer, nextSession.Length));
        Assert.True(SpinWait.SpinUntil(() => dut.Idle, OperationTimeout));
        Assert.Equal(0, peer.BytesToRead);
    }

    [Fact]
    public void Many_lifecycle_cycles_do_not_degrade_or_leave_port_unavailable()
    {
        var hardware = RequireHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateDut(hardware.DutPort);

        for (var cycle = 0; cycle < 100; cycle++)
        {
            Assert.True(dut.Connect());
            var payload = TaggedMessage(cycle, 16, 0x50);
            Assert.True(dut.Command(payload));
            Assert.Equal(payload, ReadExactly(peer, payload.Length));
            Assert.True(SpinWait.SpinUntil(() => dut.Idle, OperationTimeout));
            Assert.True(dut.Disconnect());
            Assert.Equal(SerialConnectionState.Disconnected, dut.ConnectionState);
            Assert.False(dut.ConnectionRequested);
            Assert.False(dut.Ready);
            DrainPeer(peer);
        }

        AssertPortCanBeReopened(hardware.DutPort);
    }

    [Fact]
    public async Task Repeated_traffic_and_reconnect_cycles_preserve_message_boundaries_and_session_identity()
    {
        var hardware = RequireHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateDut(hardware.DutPort);

        for (var cycle = 0; cycle < 40; cycle++)
        {
            Assert.True(dut.Connect());

            var fromDut = TaggedMessage(cycle, 24, 0x20);
            Assert.True(dut.Command(fromDut));
            Assert.Equal(fromDut, ReadExactly(peer, fromDut.Length));
            Assert.True(SpinWait.SpinUntil(() => dut.Idle, OperationTimeout));

            var fromPeer = TaggedMessage(cycle, 24, 0xA0);
            var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(ReceivedData data) => received.TrySetResult(data.PayloadBytes.ToArray());
            dut.DataReceived += Handler;
            try
            {
                peer.Write(fromPeer, 0, fromPeer.Length);
                Assert.Equal(fromPeer, await received.Task.WaitAsync(OperationTimeout));
            }
            finally { dut.DataReceived -= Handler; }

            Assert.True(dut.Disconnect());
            DrainPeer(peer);
        }

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

    private static SerialChannel CreateSlowDut(string portName) => new(new SerialPortSettings(portName, BaudRate))
    {
        MaximumMillisecondsSilenceInMessage = 20,
        MillisecondsBetweenMessages = -1,
        MillisecondsBetweenBytes = SlowBytePacingMilliseconds
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
        var progress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(() =>
        {
            var block = Sequence(0x40, 64);
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    peer.Write(block, 0, block.Length);
                    progress.TrySetResult();
                }
                catch (Exception) when (cancellation.IsCancellationRequested || !peer.IsOpen) { break; }
            }
        });
        return new PeerTraffic(cancellation, task, progress);
    }

    private static async Task AssertCompletesWithin(Action action)
    {
        var task = Task.Run(action);
        await task.WaitAsync(OperationTimeout);
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

    private static void DrainPeer(SerialPort port)
    {
        while (port.BytesToRead > 0)
        {
            var count = Math.Min(port.BytesToRead, 256);
            var buffer = new byte[count];
            _ = port.Read(buffer, 0, buffer.Length);
        }
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

    private static byte[] TaggedMessage(int identity, int count, byte seed)
    {
        var result = Sequence(seed, count);
        result[0] = 0xA5;
        result[1] = unchecked((byte)identity);
        result[2] = unchecked((byte)~identity);
        result[^1] = 0x5A;
        return result;
    }

    private sealed record HardwarePair(string DutPort, string PeerPort);

    private sealed class PeerTraffic(CancellationTokenSource cancellation, Task task, TaskCompletionSource progress) : IDisposable
    {
        public TaskCompletionSource Progress { get; } = progress;

        public void Dispose()
        {
            cancellation.Cancel();
            try { task.Wait(OperationTimeout); }
            catch (AggregateException e) when (e.InnerExceptions.All(x => x is OperationCanceledException)) { }
            cancellation.Dispose();
        }
    }
}

using System.Diagnostics;
using System.IO.Ports;
using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

/// <summary>
/// Opt-in, operator-coordinated tests for actual serial-device removal and reattachment.
/// The operator supplies the physical fault; the test detects disappearance/reappearance and asserts the same
/// SerialChannel behavior on every platform.
/// </summary>
public sealed class SerialPortPhysicalLossIntegrationTests
{
    private const int BaudRate = 9600;
    private const int SlowBytePacingMilliseconds = 10;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OperatorTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Unplug_while_idle_transitions_to_unavailable_without_losing_connection_intent()
    {
        var hardware = RequirePhysicalLossHardware();
        using var dut = CreateDut(hardware.DutPort);
        Assert.True(dut.Connect());
        Assert.Equal(SerialConnectionState.Connected, dut.ConnectionState);

        Prompt($"UNPLUG DUT adapter {hardware.DutPort} now. Leave peer {hardware.PeerPort} connected.");
        await WaitForPortPresence(hardware.DutPort, present: false);
        await WaitForState(dut, SerialConnectionState.Unavailable, RecoveryTimeout);

        Assert.True(dut.ConnectionRequested);
        Assert.False(dut.Ready);
    }

    [Fact]
    public async Task Unplug_during_receive_releases_stale_transport_and_recovers_cleanly()
    {
        var hardware = RequirePhysicalLossHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateDut(hardware.DutPort);
        Assert.True(dut.Connect());

        using var traffic = StartPeerTraffic(peer);
        await traffic.Progress.Task.WaitAsync(OperationTimeout);
        Prompt($"UNPLUG DUT adapter {hardware.DutPort} now while receive traffic is active.");
        await WaitForPortPresence(hardware.DutPort, present: false);
        await WaitForState(dut, SerialConnectionState.Unavailable, RecoveryTimeout);
        Assert.True(dut.ConnectionRequested);

        traffic.Stop();
        Prompt($"REPLUG DUT adapter {hardware.DutPort} now.");
        await WaitForPortPresence(hardware.DutPort, present: true);
        await WaitForState(dut, SerialConnectionState.Connected, RecoveryTimeout);
        Assert.True(dut.Ready);

        var payload = Sequence(0x81, 32);
        Assert.True(dut.Command(payload));
        Assert.Equal(payload, ReadExactly(peer, payload.Length));
    }

    [Fact]
    public async Task Unplug_during_transmit_releases_stale_transport_and_recovers_cleanly()
    {
        var hardware = RequirePhysicalLossHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateSlowDut(hardware.DutPort);
        Assert.True(dut.Connect());

        var payload = Sequence(0x31, 240);
        Assert.True(dut.Command(payload));
        var prefix = ReadExactly(peer, 8);
        Assert.Equal(payload[..prefix.Length], prefix);
        Assert.True(dut.HaveWork);

        Prompt($"UNPLUG DUT adapter {hardware.DutPort} now while transmit traffic is active.");
        await WaitForPortPresence(hardware.DutPort, present: false);
        await WaitForState(dut, SerialConnectionState.Unavailable, RecoveryTimeout);
        Assert.True(dut.ConnectionRequested);

        Prompt($"REPLUG DUT adapter {hardware.DutPort} now.");
        await WaitForPortPresence(hardware.DutPort, present: true);
        await WaitForState(dut, SerialConnectionState.Connected, RecoveryTimeout);
        DrainPeer(peer);

        var recovered = Sequence(0xC1, 24);
        Assert.True(dut.Command(recovered));
        Assert.Equal(recovered, ReadExactly(peer, recovered.Length));
    }

    [Fact]
    public async Task Replug_same_port_name_acquires_a_fresh_usable_transport()
    {
        var hardware = RequirePhysicalLossHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateDut(hardware.DutPort);
        Assert.True(dut.Connect());

        var before = TaggedMessage(1, 24, 0x20);
        Assert.True(dut.Command(before));
        Assert.Equal(before, ReadExactly(peer, before.Length));

        Prompt($"UNPLUG DUT adapter {hardware.DutPort} now.");
        await WaitForPortPresence(hardware.DutPort, present: false);
        await WaitForState(dut, SerialConnectionState.Unavailable, RecoveryTimeout);

        Prompt($"REPLUG the same DUT adapter and wait for {hardware.DutPort} to return.");
        await WaitForPortPresence(hardware.DutPort, present: true);
        await WaitForState(dut, SerialConnectionState.Connected, RecoveryTimeout);

        var after = TaggedMessage(2, 24, 0x60);
        Assert.True(dut.Command(after));
        Assert.Equal(after, ReadExactly(peer, after.Length));
        Assert.True(dut.Ready);
    }

    [Fact]
    public async Task Rapid_unplug_replug_same_port_name_does_not_preserve_a_stale_session()
    {
        var hardware = RequirePhysicalLossHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateDut(hardware.DutPort);
        Assert.True(dut.Connect());

        var before = TaggedMessage(3, 24, 0x30);
        Assert.True(dut.Command(before));
        Assert.Equal(before, ReadExactly(peer, before.Length));

        Prompt($"UNPLUG DUT {hardware.DutPort}; as soon as the OS removes the port, REPLUG it immediately.");
        await WaitForPortPresence(hardware.DutPort, present: false);
        await WaitForPortPresence(hardware.DutPort, present: true);
        await WaitForState(dut, SerialConnectionState.Connected, RecoveryTimeout);
        DrainPeer(peer);

        var after = TaggedMessage(4, 24, 0x70);
        Assert.True(dut.Command(after));
        Assert.Equal(after, ReadExactly(peer, after.Length));
        Assert.True(dut.Ready);
    }

    [Fact]
    public async Task Data_from_before_transport_loss_is_not_delivered_as_part_of_recovered_session()
    {
        var hardware = RequirePhysicalLossHardware();
        using var peer = OpenPeer(hardware.PeerPort);
        using var dut = CreateDut(hardware.DutPort);
        Assert.True(dut.Connect());

        var delivered = new List<byte[]>();
        var gate = new object();
        dut.DataReceived += data =>
        {
            lock (gate) delivered.Add(data.PayloadBytes.ToArray());
        };

        // Send less than one silence-delimited message and remove the transport before intentionally starting
        // the recovered session. Any old partial receive state must die with the old transport/session.
        peer.Write(new byte[] { 0xA5, 0x11, 0xEE, 0x12 }, 0, 4);
        Prompt($"UNPLUG DUT adapter {hardware.DutPort} now, then leave it unplugged until prompted.");
        await WaitForPortPresence(hardware.DutPort, present: false);
        await WaitForState(dut, SerialConnectionState.Unavailable, RecoveryTimeout);

        lock (gate) delivered.Clear();
        Prompt($"REPLUG DUT adapter {hardware.DutPort} now.");
        await WaitForPortPresence(hardware.DutPort, present: true);
        await WaitForState(dut, SerialConnectionState.Connected, RecoveryTimeout);

        var recovered = TaggedMessage(5, 24, 0xB0);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ReceivedData data) => received.TrySetResult(data.PayloadBytes.ToArray());
        dut.DataReceived += Handler;
        try
        {
            peer.Write(recovered, 0, recovered.Length);
            Assert.Equal(recovered, await received.Task.WaitAsync(OperationTimeout));
        }
        finally { dut.DataReceived -= Handler; }

        lock (gate)
            Assert.DoesNotContain(delivered, message => message.Length > recovered.Length || !message.SequenceEqual(recovered));
    }

    private static HardwarePair RequirePhysicalLossHardware()
    {
        var enabled = Environment.GetEnvironmentVariable("SERIAL_TEST_PHYSICAL_LOSS");
        if (!string.Equals(enabled, "1", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("Set SERIAL_TEST_PHYSICAL_LOSS=1 to run operator-coordinated physical removal tests.");

        var dut = Environment.GetEnvironmentVariable("SERIAL_TEST_DUT_PORT");
        var peer = Environment.GetEnvironmentVariable("SERIAL_TEST_PEER_PORT");
        if (string.IsNullOrWhiteSpace(dut) || string.IsNullOrWhiteSpace(peer))
            Assert.Skip("Set SERIAL_TEST_DUT_PORT and SERIAL_TEST_PEER_PORT to run real serial transport tests.");

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

    private static async Task WaitForPortPresence(string portName, bool present)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < OperatorTimeout)
        {
            var found = SerialPort.GetPortNames().Contains(portName, StringComparer.OrdinalIgnoreCase);
            if (found == present) return;
            await Task.Delay(50);
        }
        Assert.Fail($"Timed out waiting for {portName} to become {(present ? "present" : "absent")}.");
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

    private static void Prompt(string text)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"*** SERIAL HARDWARE ACTION: {text}");
        Console.Error.WriteLine($"*** Waiting up to {OperatorTimeout.TotalSeconds:0} seconds for the OS-visible port transition.");
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

        public void Stop()
        {
            cancellation.Cancel();
            try { task.Wait(OperationTimeout); }
            catch (AggregateException e) when (e.InnerExceptions.All(x => x is OperationCanceledException)) { }
        }

        public void Dispose()
        {
            Stop();
            cancellation.Dispose();
        }
    }
}

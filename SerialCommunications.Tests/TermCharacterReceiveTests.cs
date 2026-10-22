using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class TermCharacterReceiveTests
{
    private static CrcOptions TerminatedCrc() => new()
    {
        Polynomial = 0xA001, InitialValue = 0xFFFF, ExpectedResidue = 0,
        PostInvert = false, MsBitFirst = false, MsByteFirst = false,
        TermChar = 0x03, OmitTermChar = false
    };

    private static CrcOptions AeonTerminatedCrc() => new()
    {
        Polynomial = 0xDAAE, InitialValue = 0xFFFF, ExpectedResidue = 0x82C0,
        PostInvert = true, MsBitFirst = false, MsByteFirst = false,
        TermChar = 0x03, OmitTermChar = false
    };

    [Fact]
    public void Parser_accepts_ordinary_valid_message()
    {
        var options = TerminatedCrc(); var payload = new byte[] { 0x10, 0x20, 0x30 };
        using var harness = new ReceiveHarness(options); harness.Feed(new Crc(options).Append(payload));
        var received = harness.WaitForMessages(1).Single();
        Assert.True(received.CrcValid); Assert.Equal(payload, received.PayloadBytes.ToArray());
    }

    [Fact]
    public void Aeon_crc_with_post_invert_and_term_character_is_decoded()
    {
        var options = AeonTerminatedCrc();
        var payload = System.Text.Encoding.ASCII.GetBytes("r\r\n");
        var wire = new Crc(options).Append(payload);
        Assert.Equal(options.TermChar, wire[^1]);

        using var harness = new ReceiveHarness(options); harness.Feed(wire);
        var received = harness.WaitForMessages(1).Single();

        Assert.True(received.CrcValid);
        Assert.Equal(payload, received.PayloadBytes.ToArray());
        Assert.Equal(options.ExpectedResidue, received.ExpectedResidue);
        Assert.Equal(options.ExpectedResidue, received.CalculatedResidue);
    }

    [Fact]
    public void Aeon_crc_allows_term_character_in_either_crc_byte()
    {
        var options = AeonTerminatedCrc();
        foreach (var index in new[] { 3, 2 })
        {
            var (payload, wire) = FindCodeword(options, bytes => bytes[^index] == options.TermChar);
            Assert.Equal(options.TermChar, wire[^1]);
            using var harness = new ReceiveHarness(options); harness.Feed(wire);
            var received = harness.WaitForMessages(1).Single();
            Assert.True(received.CrcValid); Assert.Equal(payload, received.PayloadBytes.ToArray());
        }
    }

    [Fact]
    public void Illegal_term_character_in_payload_is_rejected_and_parser_recovers()
    {
        var options = TerminatedCrc();
        var invalid = new Crc(options).Append(new byte[] { 0x10, options.TermChar, 0x20, 0x30 });
        var payload = new byte[] { 0x41, 0x42, 0x43 }; var valid = new Crc(options).Append(payload);
        using var harness = new ReceiveHarness(options); harness.Feed(invalid.Concat(valid).ToArray());
        var received = harness.WaitForMessages(3);
        Assert.False(received[0].CheckValid);
        Assert.False(received[1].CheckValid);
        Assert.True(received[2].CheckValid);
        Assert.Equal(payload, received[2].PayloadBytes.ToArray());
        Assert.Equal(2, harness.CheckFailures);
    }

    [Fact]
    public void Parser_handles_term_character_in_either_crc_byte()
    {
        var options = TerminatedCrc();
        foreach (var index in new[] { 3, 2 })
        {
            var (payload, wire) = FindCodeword(options, bytes => bytes[^index] == options.TermChar);
            using var harness = new ReceiveHarness(options); harness.Feed(wire);
            var received = harness.WaitForMessages(1).Single();
            Assert.True(received.CrcValid); Assert.Equal(payload, received.PayloadBytes.ToArray());
        }
    }

    [Fact]
    public void Parser_preserves_state_across_every_fragment_boundary()
    {
        var options = TerminatedCrc(); var (payload, wire) = FindCodeword(options, bytes => bytes[^3] == options.TermChar);
        for (var split = 1; split < wire.Length; split++)
        {
            using var harness = new ReceiveHarness(options); harness.Feed(wire[..split]); harness.WaitUntilDrained(); harness.Feed(wire[split..]);
            var received = harness.WaitForMessages(1).Single(); Assert.True(received.CrcValid); Assert.Equal(payload, received.PayloadBytes.ToArray());
        }
    }

    [Fact]
    public void Parser_delivers_legal_back_to_back_messages_from_one_chunk()
    {
        var options = TerminatedCrc(); var firstPayload = new byte[] { 0x10, 0x20, 0x30 }; var secondPayload = new byte[] { 0x41, 0x42, 0x43 };
        var combined = new Crc(options).Append(firstPayload).Concat(new Crc(options).Append(secondPayload)).ToArray();
        using var harness = new ReceiveHarness(options); harness.Feed(combined); var received = harness.WaitForMessages(2);
        Assert.Equal(firstPayload, received[0].PayloadBytes.ToArray()); Assert.Equal(secondPayload, received[1].PayloadBytes.ToArray()); Assert.All(received, m => Assert.True(m.CrcValid));
    }

    [Fact]
    public void Realistic_multiline_payload_survives_fragmentation()
    {
        var options = TerminatedCrc(); var payload = System.Text.Encoding.ASCII.GetBytes(" 23.4  24.1  25.0\r\nM  50.00 a  37.25\r\n 22.8  23.0\r\n"); var wire = new Crc(options).Append(payload);
        for (var split = 1; split < wire.Length; split++)
        {
            using var harness = new ReceiveHarness(options); harness.Feed(wire[..split]); harness.WaitUntilDrained(); harness.Feed(wire[split..]);
            Assert.Equal(payload, harness.WaitForMessages(1).Single().PayloadBytes.ToArray());
        }
    }

    [Fact]
    public void Staged_protocol_change_finishes_current_message_with_old_settings_and_applies_to_next()
    {
        var oldOptions = TerminatedCrc(); var newOptions = TerminatedCrc(); newOptions.Polynomial = 0x1021;
        var oldPayload = new byte[] { 0x41, 0x42, 0x43, 0x44 }; var newPayload = new byte[] { 0x51, 0x52, 0x53, 0x54 };
        var oldWire = new Crc(oldOptions).Append(oldPayload); var newWire = new Crc(newOptions).Append(newPayload);
        using var harness = new ReceiveHarness(oldOptions); harness.Feed(oldWire[..2]); harness.WaitUntilDrained();
        Assert.True(harness.StageProtocolSettings(SerialProtocolSettings.From(newOptions, false))); harness.Feed(oldWire[2..]);
        var first = harness.WaitForMessages(1).Single(); Assert.True(first.CrcValid); Assert.Equal(oldPayload, first.PayloadBytes.ToArray());
        Assert.Equal(newOptions.Polynomial, harness.ProtocolSettings.Polynomial);
        harness.Feed(newWire); var received = harness.WaitForMessages(2); Assert.True(received[1].CrcValid); Assert.Equal(newPayload, received[1].PayloadBytes.ToArray());
    }

    private static (byte[] Payload, byte[] Wire) FindCodeword(CrcOptions options, Func<byte[], bool> predicate)
    {
        for (var value = 0; value <= ushort.MaxValue; value++)
        {
            var payload = new byte[] { (byte)value, (byte)(value >> 8) }; if (payload.Contains(options.TermChar)) continue;
            var wire = new Crc(options).Append(payload); if (predicate(wire)) return (payload, wire);
        }
        throw new InvalidOperationException("Unable to synthesize requested CRC edge case.");
    }

    private sealed class ReceiveHarness : IDisposable
    {
        private const int WaitMilliseconds = 2000;
        private readonly SerialChannel device;
        private readonly FakeSerialTransport transport;
        private readonly List<ReceivedData> messages = [];
        private readonly object sync = new();

        public ReceiveHarness(CrcOptions options)
        {
            var factory = new FakeSerialTransportFactory(); transport = factory.Enqueue();
            var configuration = new SerialChannelConfiguration(new SerialPortSettings("TEST", 115200))
            {
                ProtocolSettings = SerialProtocolSettings.From(options, false)
            };
            device = new SerialChannel(configuration, factory);
            device.DataReceived += OnDataReceived; Assert.True(device.Connect());
        }

        public long CheckFailures => device.Telemetry.CheckFailures;
        public SerialProtocolSettings ProtocolSettings => device.ProtocolSettings;
        public bool StageProtocolSettings(SerialProtocolSettings settings) => device.StageProtocolSettings(settings);
        public void Feed(ReadOnlySpan<byte> bytes) { transport.EnqueueReceived(bytes.ToArray()); transport.RaiseDataReceived(); }

        public void WaitUntilDrained()
        {
            var deadline = Environment.TickCount64 + WaitMilliseconds;
            while (transport.BytesToRead != 0 && Environment.TickCount64 < deadline) Thread.Yield();
            if (transport.BytesToRead != 0) throw new TimeoutException("Receive transport did not drain.");
        }

        public IReadOnlyList<ReceivedData> WaitForMessages(int count)
        {
            var deadline = Environment.TickCount64 + WaitMilliseconds;
            lock (sync)
            {
                while (messages.Count < count)
                {
                    var remaining = deadline - Environment.TickCount64; if (remaining <= 0) throw new TimeoutException($"Expected {count} messages; received {messages.Count}.");
                    Monitor.Wait(sync, (int)Math.Min(remaining, int.MaxValue));
                }
                return messages.ToArray();
            }
        }

        private void OnDataReceived(ReceivedData data) { lock (sync) { messages.Add(data); Monitor.PulseAll(sync); } }
        public void Dispose() { device.DataReceived -= OnDataReceived; device.Dispose(); }
    }
}

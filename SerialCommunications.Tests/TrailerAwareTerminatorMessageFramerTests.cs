using SerialCommunications.Serial;
using SerialCommunications.Serial.Protocol;

namespace SerialCommunications.Tests;

public sealed class TrailerAwareTerminatorMessageFramerTests
{
    private static CrcOptions TestCrc() => new()
    {
        Polynomial = 0xA001,
        InitialValue = 0xFFFF,
        ExpectedResidue = 0x0000,
        PostInvert = false,
        MsBitFirst = false,
        MsByteFirst = false,
        TermChar = 0x03,
        OmitTermChar = false
    };

    [Fact]
    public void Valid_frame_is_independent_of_transport_chunking()
    {
        var options = TestCrc();
        var payload = FindPayload(options, frame => frame[^2] == options.TermChar);
        var wire = new Crc(options).Append(payload);

        for (var split = 0; split <= wire.Length; split++)
        {
            var framer = CreateFramer(options);
            var frames = new List<byte[]>();
            framer.Consume(wire.AsSpan(0, split), frame => frames.Add(frame.ToArray()));
            framer.Consume(wire.AsSpan(split), frame => frames.Add(frame.ToArray()));

            Assert.Equal(wire[..^1], Assert.Single(frames));
        }
    }

    [Fact]
    public void Both_crc_positions_may_equal_terminator()
    {
        var options = TestCrc();
        foreach (var crcIndexFromEnd in new[] { 3, 2 })
        {
            var payload = FindPayload(options, wire => wire[^crcIndexFromEnd] == options.TermChar);
            var wire = new Crc(options).Append(payload);
            var framer = CreateFramer(options);
            var frames = new List<byte[]>();

            foreach (var value in wire)
                framer.Consume(new[] { value }, frame => frames.Add(frame.ToArray()));

            Assert.Equal(wire[..^1], Assert.Single(frames));
        }
    }

    [Fact]
    public void Long_payload_without_terminators_remains_chunk_invariant()
    {
        var options = TestCrc();
        var payload = Enumerable.Repeat((byte)0x41, 8192).ToArray();
        var wire = new Crc(options).Append(payload);
        Assert.DoesNotContain(options.TermChar, payload);

        var wholeFrames = new List<byte[]>();
        var whole = CreateFramer(options);
        whole.Consume(wire, frame => wholeFrames.Add(frame.ToArray()));

        var byteFrames = new List<byte[]>();
        var bytewise = CreateFramer(options);
        foreach (var value in wire)
            bytewise.Consume(new[] { value }, frame => byteFrames.Add(frame.ToArray()));

        Assert.Equal(wire[..^1], Assert.Single(wholeFrames));
        Assert.Equal(wholeFrames[0], Assert.Single(byteFrames));
    }

    [Fact]
    public void Bad_crc_with_intact_terminator_is_still_a_frame()
    {
        var options = TestCrc();
        var wire = new Crc(options).Append(new byte[] { 0x10, 0x20, 0x30 });
        wire[1] ^= 0x01;
        var framer = CreateFramer(options);
        var frames = new List<byte[]>();

        framer.Consume(wire, frame => frames.Add(frame.ToArray()));
        framer.SilenceElapsed(frame => frames.Add(frame.ToArray()));

        var frame = Assert.Single(frames);
        Assert.Equal(wire[..^1], frame);
        Assert.False(new Crc16MessageCheck(options).TryValidate(frame, out _));
    }

    [Fact]
    public void Unexpected_payload_terminator_does_not_hide_following_valid_message()
    {
        var options = TestCrc();
        var illegalPayload = new byte[] { 0x10, options.TermChar, 0x20, 0x30 };
        var invalidWire = new Crc(options).Append(illegalPayload);
        var valid = new Crc(options).Append(new byte[] { 0x41, 0x42, 0x43 });
        var framer = CreateFramer(options);
        var frames = new List<byte[]>();

        framer.Consume(invalidWire.Concat(valid).ToArray(), frame => frames.Add(frame.ToArray()));

        Assert.Equal(3, frames.Count);
        Assert.Equal(new byte[] { 0x10 }, frames[0]);
        Assert.False(new Crc16MessageCheck(options).TryValidate(frames[0], out _));
        Assert.False(new Crc16MessageCheck(options).TryValidate(frames[1], out _));
        Assert.Equal(valid[..^1], frames[2]);
    }

    [Fact]
    public void Bad_crc_frame_does_not_hide_following_valid_message()
    {
        var options = TestCrc();
        var corrupt = new Crc(options).Append(new byte[] { 0x10, 0x20, 0x30 });
        corrupt[1] ^= 0x01;
        var valid = new Crc(options).Append(new byte[] { 0x51, 0x52, 0x53 });
        var framer = CreateFramer(options);
        var frames = new List<byte[]>();

        framer.Consume(corrupt.Concat(valid).ToArray(), frame => frames.Add(frame.ToArray()));

        Assert.Equal(2, frames.Count);
        Assert.Equal(corrupt[..^1], frames[0]);
        Assert.Equal(valid[..^1], frames[1]);
        Assert.False(new Crc16MessageCheck(options).TryValidate(frames[0], out _));
        Assert.True(new Crc16MessageCheck(options).TryValidate(frames[1], out _));
    }

    [Fact]
    public void Missing_final_terminator_can_still_sacrifice_following_message()
    {
        var options = TestCrc();
        var unterminated = new Crc(options).Append(new byte[] { 0x10, 0x20, 0x30 })[..^1];
        var sacrificed = new Crc(options).Append(new byte[] { 0x41, 0x42, 0x43 });
        var recovered = new Crc(options).Append(new byte[] { 0x51, 0x52, 0x53 });
        var framer = CreateFramer(options);
        var frames = new List<byte[]>();

        framer.Consume(unterminated.Concat(sacrificed).Concat(recovered).ToArray(), frame => frames.Add(frame.ToArray()));

        Assert.Equal(2, frames.Count);
        Assert.Equal(unterminated.Concat(sacrificed[..^1]).ToArray(), frames[0]);
        Assert.Equal(recovered[..^1], frames[1]);
    }

    [Fact]
    public void Back_to_back_frames_are_delivered_from_one_chunk()
    {
        var options = TestCrc();
        var first = new Crc(options).Append(new byte[] { 0x10, 0x20 });
        var second = new Crc(options).Append(new byte[] { 0x30, 0x40 });
        var framer = CreateFramer(options);
        var frames = new List<byte[]>();

        framer.Consume(first.Concat(second).ToArray(), frame => frames.Add(frame.ToArray()));

        Assert.Equal(2, frames.Count);
        Assert.Equal(first[..^1], frames[0]);
        Assert.Equal(second[..^1], frames[1]);
    }

    private static TrailerAwareTerminatorMessageFramer CreateFramer(CrcOptions options) =>
        new(options.TermChar, new Crc16MessageCheck(options));

    private static byte[] FindPayload(CrcOptions options, Func<byte[], bool> predicate)
    {
        for (var value = 0; value <= ushort.MaxValue; value++)
        {
            var payload = new byte[] { (byte)value, (byte)(value >> 8) };
            if (payload.Contains(options.TermChar)) continue;
            var wire = new Crc(options).Append(payload);
            if (predicate(wire)) return payload;
        }

        throw new InvalidOperationException("Unable to synthesize requested CRC edge case.");
    }
}

using SerialCommunications.Serial.Protocol;

namespace SerialCommunications.Tests;

public sealed class MessageFramerTests
{
    [Fact]
    public void Terminator_framing_is_independent_of_transport_chunking()
    {
        byte[] wire = [0x41, 0x42, 0x03, 0x43, 0x44, 0x03];
        var expected = Decode(new TerminatorMessageFramer(0x03), [wire]);

        Assert.Equal(2, expected.Count);
        Assert.Equal(new byte[] { 0x41, 0x42 }, expected[0]);
        Assert.Equal(new byte[] { 0x43, 0x44 }, expected[1]);

        for (var split = 1; split < wire.Length; split++)
        {
            var actual = Decode(new TerminatorMessageFramer(0x03), [wire[..split], wire[split..]]);
            AssertFramesEqual(expected, actual);
        }

        var byteAtATime = wire.Select(value => new[] { value }).ToArray();
        AssertFramesEqual(expected, Decode(new TerminatorMessageFramer(0x03), byteAtATime));
    }

    [Fact]
    public void Silence_framing_does_not_confuse_chunk_delivery_with_message_boundaries()
    {
        var framer = new SilenceMessageFramer();
        var frames = new List<byte[]>();
        void Ready(ReadOnlyMemory<byte> frame) => frames.Add(frame.ToArray());

        framer.Consume(new byte[] { 0x48, 0x65 }, Ready);
        framer.Consume(new byte[] { 0x6C, 0x6C }, Ready);
        framer.Consume(new byte[] { 0x6F, 0x21 }, Ready);
        Assert.Empty(frames);

        framer.SilenceElapsed(Ready);
        var frame = Assert.Single(frames);
        Assert.Equal(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x21 }, frame);
    }

    [Fact]
    public void Terminator_framing_ignores_silence_notifications()
    {
        var framer = new TerminatorMessageFramer(0x03);
        var frames = new List<byte[]>();
        void Ready(ReadOnlyMemory<byte> frame) => frames.Add(frame.ToArray());

        framer.Consume(new byte[] { 0x41 }, Ready);
        framer.SilenceElapsed(Ready);
        framer.Consume(new byte[] { 0x42, 0x03 }, Ready);

        var frame = Assert.Single(frames);
        Assert.Equal(new byte[] { 0x41, 0x42 }, frame);
    }

    private static List<byte[]> Decode(IMessageFramer framer, IEnumerable<byte[]> chunks)
    {
        var frames = new List<byte[]>();
        foreach (var chunk in chunks)
            framer.Consume(chunk, frame => frames.Add(frame.ToArray()));
        return frames;
    }

    private static void AssertFramesEqual(IReadOnlyList<byte[]> expected, IReadOnlyList<byte[]> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) Assert.Equal(expected[i], actual[i]);
    }
}

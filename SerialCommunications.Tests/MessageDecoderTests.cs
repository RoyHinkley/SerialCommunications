using SerialCommunications.Serial;
using SerialCommunications.Serial.Protocol;

namespace SerialBench.Tests;

public sealed class MessageDecoderTests
{
    private static CrcOptions TestCrcOptions() => new()
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
    public void Decoder_preserves_frame_and_extracts_valid_payload()
    {
        var check = new Crc16MessageCheck(TestCrcOptions());
        var decoder = new MessageDecoder(new TrailerAwareTerminatorMessageFramer(0x03, check), check);
        var payload = new byte[] { 0x41, 0x42, 0x43 };
        var wire = check.Append(payload).Append((byte)0x03).ToArray();
        var messages = new List<DecodedMessage>();

        decoder.Consume(wire, messages.Add);

        var message = Assert.Single(messages);
        Assert.True(message.CheckValid);
        Assert.Equal(check.Append(payload), message.Frame.ToArray());
        Assert.Equal(payload, message.Payload.ToArray());
    }

    [Fact]
    public void Bad_check_preserves_frame_and_candidate_payload()
    {
        var check = new Crc16MessageCheck(TestCrcOptions());
        var decoder = new MessageDecoder(new TrailerAwareTerminatorMessageFramer(0x03, check), check);
        var payload = new byte[] { 0x31, 0x32, 0x33 };
        var badFrame = check.Append(payload);
        badFrame[0] ^= 0x01;
        var expectedPayload = badFrame[..^check.FieldLength];
        var wire = badFrame.Append((byte)0x03).Concat(new byte[] { 0x55, 0x66, 0x77 }).ToArray();
        var messages = new List<DecodedMessage>();

        decoder.Consume(wire, messages.Add);

        var message = Assert.Single(messages);
        Assert.False(message.CheckValid);
        Assert.Equal(badFrame, message.Frame.ToArray());
        Assert.Equal(expectedPayload, message.Payload.ToArray());
    }

    [Fact]
    public void No_check_decoder_supports_plain_terminated_protocols()
    {
        var decoder = new MessageDecoder(new TerminatorMessageFramer(0x0A), NoMessageCheck.Instance);
        var messages = new List<DecodedMessage>();

        decoder.Consume(new byte[] { 0x41, 0x42, 0x0A, 0x43, 0x0A }, messages.Add);

        Assert.Equal(2, messages.Count);
        Assert.All(messages, message => Assert.True(message.CheckValid));
        Assert.Equal(new byte[] { 0x41, 0x42 }, messages[0].Payload.ToArray());
        Assert.Equal(new byte[] { 0x43 }, messages[1].Payload.ToArray());
    }

    [Fact]
    public void Decoder_result_is_independent_of_transport_chunking()
    {
        var check = new Crc16MessageCheck(TestCrcOptions());
        var first = check.Append(new byte[] { 0x10, 0x20 });
        var second = check.Append(new byte[] { 0x30, 0x40, 0x50 });
        var wire = first.Append((byte)0x03).Concat(second).Append((byte)0x03).ToArray();

        var whole = Decode(wire, wire.Length);
        var bytewise = Decode(wire, 1);

        Assert.Equal(whole.Count, bytewise.Count);
        for (var i = 0; i < whole.Count; i++)
        {
            Assert.Equal(whole[i].CheckValid, bytewise[i].CheckValid);
            Assert.Equal(whole[i].Frame.ToArray(), bytewise[i].Frame.ToArray());
            Assert.Equal(whole[i].Payload.ToArray(), bytewise[i].Payload.ToArray());
        }
    }

    [Fact]
    public void Silence_is_forwarded_only_as_an_explicit_framing_event()
    {
        var decoder = new MessageDecoder(new SilenceMessageFramer(), NoMessageCheck.Instance);
        var messages = new List<DecodedMessage>();

        decoder.Consume("He"u8, messages.Add);
        decoder.Consume("ll"u8, messages.Add);
        decoder.Consume("o!"u8, messages.Add);
        Assert.Empty(messages);

        decoder.SilenceElapsed(messages.Add);

        var message = Assert.Single(messages);
        Assert.Equal("Hello!"u8.ToArray(), message.Payload.ToArray());
    }

    private static List<DecodedMessage> Decode(byte[] wire, int chunkSize)
    {
        var check = new Crc16MessageCheck(TestCrcOptions());
        var decoder = new MessageDecoder(new TrailerAwareTerminatorMessageFramer(0x03, check), check);
        var messages = new List<DecodedMessage>();

        for (var offset = 0; offset < wire.Length; offset += chunkSize)
            decoder.Consume(wire.AsSpan(offset, Math.Min(chunkSize, wire.Length - offset)), messages.Add);

        return messages;
    }
}

using SerialCommunications.Serial;

namespace SerialBench.Tests;

public sealed class TermCharacterFramingTests
{
    private static CrcOptions TerminatedCrc() => new()
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
    public void Codeword_places_crc_before_term_character()
    {
        var options = TerminatedCrc();
        var payload = new byte[] { 0x10, 0x20, 0x30 };

        var wire = new Crc(options).Append(payload);

        Assert.Equal(options.TermChar, wire[^1]);
        Assert.Equal(payload, wire[..payload.Length]);
        Assert.Equal(payload.Length + 3, wire.Length);
        Assert.True(CodewordCrcIsValid(wire, options));
    }

    [Fact]
    public void Embedded_term_character_in_payload_does_not_define_message_end()
    {
        var options = TerminatedCrc();
        var payload = new byte[] { 0x10, 0x03, 0x20, 0x03, 0x30 };

        var wire = new Crc(options).Append(payload);

        Assert.Equal(3, wire.Count(value => value == options.TermChar));
        Assert.Equal(options.TermChar, wire[^1]);
        Assert.True(CodewordCrcIsValid(wire, options));
    }

    [Fact]
    public void Low_crc_byte_can_equal_term_character()
    {
        var options = TerminatedCrc();
        var (payload, wire) = FindCodeword(options, bytes => bytes[^3] == options.TermChar);

        Assert.Equal(options.TermChar, wire[^3]);
        Assert.Equal(options.TermChar, wire[^1]);
        Assert.True(CodewordCrcIsValid(wire, options));
        Assert.NotEmpty(payload);
    }

    [Fact]
    public void High_crc_byte_can_equal_term_character()
    {
        var options = TerminatedCrc();
        var (payload, wire) = FindCodeword(options, bytes => bytes[^2] == options.TermChar);

        Assert.Equal(options.TermChar, wire[^2]);
        Assert.Equal(options.TermChar, wire[^1]);
        Assert.True(CodewordCrcIsValid(wire, options));
        Assert.NotEmpty(payload);
    }

    [Fact]
    public void Both_crc_bytes_are_part_of_crc_validation_but_term_character_is_not()
    {
        var options = TerminatedCrc();
        var payload = new byte[] { 0x41, 0x42, 0x43 };
        var wire = new Crc(options).Append(payload);
        var crc = new Crc(options);

        crc.Init();
        crc.Update(wire.AsSpan(0, wire.Length - 1));
        Assert.True(crc.Good());

        crc.Update(wire[^1]);
        Assert.False(crc.Good());
    }

    private static bool CodewordCrcIsValid(ReadOnlySpan<byte> wire, CrcOptions options)
    {
        var crc = new Crc(options);
        crc.Init();
        crc.Update(wire[..^1]);
        return crc.Good();
    }

    private static (byte[] Payload, byte[] Wire) FindCodeword(CrcOptions options, Func<byte[], bool> predicate)
    {
        for (var value = 0; value <= ushort.MaxValue; value++)
        {
            var payload = new byte[] { (byte)value, (byte)(value >> 8) };
            var wire = new Crc(options).Append(payload);
            if (predicate(wire)) return (payload, wire);
        }

        throw new InvalidOperationException("Unable to synthesize requested CRC edge case.");
    }
}

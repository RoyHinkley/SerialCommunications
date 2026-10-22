using SerialCommunications.Serial;

namespace SerialBench.Tests;

public sealed class CrcTests
{
    /// <summary>CRC parameters used during the first Eurotherm field test.</summary>
    private static CrcOptions EurothermFieldConfiguration() => new()
    {
        Polynomial = 0xA001,
        InitialValue = 0xFFFF,
        ExpectedResidue = 0x0000,
        PostInvert = false,
        MsBitFirst = false,
        MsByteFirst = false,
        OmitTermChar = true
    };

    [Fact]
    public void Append_known_field_command_produces_expected_codeword()
    {
        var crc = new Crc(EurothermFieldConfiguration());
        var payload = new byte[] { 0x02, 0x03, 0x00, 0x01, 0x00, 0x02 };

        var codeword = crc.Append(payload);

        Assert.Equal(new byte[] { 0x02, 0x03, 0x00, 0x01, 0x00, 0x02, 0x95, 0xF8 }, codeword);
    }

    [Fact]
    public void Append_is_stateless_between_messages()
    {
        var crc = new Crc(EurothermFieldConfiguration());
        var payload = new byte[] { 0x02, 0x03, 0x00, 0x01, 0x00, 0x02 };

        var first = crc.Append(payload);
        var second = crc.Append(payload);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Incremental_update_matches_whole_buffer_update_for_every_split()
    {
        var options = EurothermFieldConfiguration();
        var bytes = new byte[] { 0x02, 0x03, 0x00, 0x01, 0x00, 0x02, 0x95, 0xF8 };
        var whole = new Crc(options);
        whole.Init();
        var expected = whole.Update(bytes);

        for (var split = 0; split <= bytes.Length; split++)
        {
            var incremental = new Crc(options);
            incremental.Init();
            incremental.Update(bytes.AsSpan(0, split));
            var actual = incremental.Update(bytes.AsSpan(split));
            Assert.Equal(expected, actual);
        }
    }
}

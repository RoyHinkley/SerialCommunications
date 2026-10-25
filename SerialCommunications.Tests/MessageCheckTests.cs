using SerialCommunications.Serial;
using SerialCommunications.Serial.Protocol;

namespace SerialCommunications.Tests;

public sealed class MessageCheckTests
{
    private static CrcOptions AeonLikeCrc() => new()
    {
        Polynomial = 0xA001,
        InitialValue = 0xFFFF,
        ExpectedResidue = 0x0000,
        PostInvert = false,
        MsBitFirst = false,
        MsByteFirst = false
    };

    [Fact]
    public void No_check_preserves_exact_payload()
    {
        var check = NoMessageCheck.Instance;
        var payload = new byte[] { 0x00, 0x03, 0xFF };

        var frame = check.Append(payload);

        Assert.Equal(payload, frame);
        Assert.True(check.TryValidate(frame, out var decoded));
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void Crc_check_appends_only_check_code()
    {
        var options = AeonLikeCrc();
        var check = new Crc16MessageCheck(options);
        var payload = new byte[] { 0x41, 0x42, 0x43 };

        var frame = check.Append(payload);

        Assert.Equal(payload.Length + 2, frame.Length);
        Assert.Equal(new Crc(options).Append(payload), frame);
        Assert.True(check.TryValidate(frame, out var decoded));
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void Crc_check_rejects_corruption_without_discarding_candidate_payload()
    {
        var check = new Crc16MessageCheck(AeonLikeCrc());
        var frame = check.Append(new byte[] { 0x10, 0x20, 0x30 });
        frame[1] ^= 0x01;

        Assert.False(check.TryValidate(frame, out var decoded));
        Assert.Equal(new byte[] { 0x10, 0x21, 0x30 }, decoded);
    }

    [Fact]
    public void Crc_check_accepts_arbitrary_byte_values_in_check_code()
    {
        const byte valueOfInterest = 0x03;
        var options = AeonLikeCrc();
        var check = new Crc16MessageCheck(options);
        var payload = FindPayload(options, frame => frame[^2] == valueOfInterest || frame[^1] == valueOfInterest);
        var frame = check.Append(payload);

        Assert.Contains(valueOfInterest, frame[^2..]);
        Assert.True(check.TryValidate(frame, out var decoded));
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void Crc_check_snapshots_mutable_options()
    {
        var options = AeonLikeCrc();
        var check = new Crc16MessageCheck(options);
        var payload = new byte[] { 0x51, 0x52, 0x53 };
        var frame = check.Append(payload);

        options.Polynomial = 0x1021;
        options.InitialValue = 0;

        Assert.True(check.TryValidate(frame, out var decoded));
        Assert.Equal(payload, decoded);
    }

    private static byte[] FindPayload(CrcOptions options, Func<byte[], bool> predicate)
    {
        for (var value = 0; value <= ushort.MaxValue; value++)
        {
            var payload = new byte[] { (byte)value, (byte)(value >> 8) };
            var frame = new Crc16MessageCheck(options).Append(payload);
            if (predicate(frame)) return payload;
        }

        throw new InvalidOperationException("Unable to synthesize requested CRC edge case.");
    }
}

using SerialCommunications.Serial;
using SerialCommunications.Serial.Protocol;

namespace SerialCommunications.Tests;

public sealed class SerialProtocolSettingsTests
{
    [Fact]
    public void Snapshot_is_independent_of_mutable_crc_options()
    {
        var options = new CrcOptions(0xFFFF, 0xDAAE, 0x82C0, true, false, false, 0x03, false);
        var snapshot = SerialProtocolSettings.Crc16(options, suppressInvalidMessages: true);

        options.Polynomial = 0xA001;
        options.TermChar = 0x04;
        options.OmitTermChar = true;

        Assert.Equal(MessageCheckKind.Crc16, snapshot.CheckKind);
        Assert.Equal((ushort)0xDAAE, snapshot.Polynomial);
        Assert.Equal((byte)0x03, snapshot.TermChar);
        Assert.False(snapshot.OmitTermChar);
        Assert.True(snapshot.SuppressInvalidMessages);
    }

    [Fact]
    public void Each_crc_options_instance_created_from_snapshot_is_independent()
    {
        var snapshot = SerialProtocolSettings.Crc16(new CrcOptions());
        var first = snapshot.CreateCrcOptions()!;
        var second = snapshot.CreateCrcOptions()!;

        first.Polynomial = 0xA001;

        Assert.NotSame(first, second);
        Assert.Equal((ushort)0xDAAE, second.Polynomial);
    }

    [Fact]
    public void Check_factories_create_expected_calculations()
    {
        var payload = new byte[] { 0x10, 0x20, 0x31 };
        var options = new CrcOptions(0xFFFF, 0xA001, 0, false, false, false, 0x03, true);

        var crc = SerialProtocolSettings.Crc16(options).CreateMessageCheck();
        var none = SerialProtocolSettings.None().CreateMessageCheck();

        Assert.IsType<Crc16MessageCheck>(crc);
        Assert.Equal(new Crc(options).Append(payload), crc.Append(payload));
        Assert.IsType<NoMessageCheck>(none);
        Assert.Equal(payload, none.Append(payload));
    }

    [Fact]
    public void Crc_parameter_changes_keep_same_receive_framing_strategy()
    {
        var current = SerialProtocolSettings.Crc16(new CrcOptions());
        var changed = current with { Polynomial = 0xA001, ExpectedResidue = 0, SuppressInvalidMessages = true };

        Assert.True(current.UsesSameFramingStrategyAs(changed));
    }

    [Fact]
    public void Changing_between_term_and_silence_framing_changes_receive_strategy()
    {
        var terminated = SerialProtocolSettings.Crc16(new CrcOptions { OmitTermChar = false });
        var silence = terminated with { OmitTermChar = true };

        Assert.False(terminated.UsesSameFramingStrategyAs(silence));
    }

    [Fact]
    public void Changing_check_kind_changes_receive_strategy()
    {
        var crc = SerialProtocolSettings.Crc16(new CrcOptions());
        var none = SerialProtocolSettings.None();

        Assert.False(crc.UsesSameFramingStrategyAs(none));
        Assert.False(none.UsesSameFramingStrategyAs(crc));
    }
}

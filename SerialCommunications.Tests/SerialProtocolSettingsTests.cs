using SerialCommunications.Serial;
using SerialCommunications.Serial.Protocol;

namespace SerialCommunications.Tests;

public sealed class SerialProtocolSettingsTests
{
    [Fact]
    public void Snapshot_is_independent_of_mutable_inputs()
    {
        var options = new CrcOptions(0xFFFF, 0xDAAE, 0x82C0, true, false, false, 0x03, false);
        byte[] end = [0x03];
        var snapshot = SerialProtocolSettings.Crc16(options, FrameLayout.PCE, end: end, suppressInvalidMessages: true);

        options.Polynomial = 0xA001;
        end[0] = 0x04;
        var returnedEnd = snapshot.End;
        returnedEnd[0] = 0x05;

        Assert.Equal((ushort)0xDAAE, snapshot.Polynomial);
        Assert.Equal(new byte[] { 0x03 }, snapshot.End);
        Assert.True(snapshot.SuppressInvalidMessages);
    }

    [Fact]
    public void Null_and_single_byte_framing_values_are_normalized()
    {
        var plain = SerialProtocolSettings.None();
        var framed = SerialProtocolSettings.None(FrameLayout.SPE, 0x02, 0x03);

        Assert.Empty(plain.Start);
        Assert.Empty(plain.End);
        Assert.Equal(new byte[] { 0x02 }, framed.Start);
        Assert.Equal(new byte[] { 0x03 }, framed.End);
    }

    [Fact]
    public void Invalid_layout_combinations_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => SerialProtocolSettings.None(FrameLayout.PE));
        Assert.Throws<ArgumentException>(() => SerialProtocolSettings.None(FrameLayout.SP));
        Assert.Throws<ArgumentException>(() => SerialProtocolSettings.None(FrameLayout.PC));
        Assert.Throws<ArgumentException>(() => SerialProtocolSettings.None(FrameLayout.P, end: [0x03]));
        Assert.Throws<ArgumentException>(() => SerialProtocolSettings.Crc16(new CrcOptions(), FrameLayout.P));
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

    public static TheoryData<FrameLayout> Layouts => new()
    {
        FrameLayout.P, FrameLayout.PE, FrameLayout.PC, FrameLayout.PEC,
        FrameLayout.SP, FrameLayout.SPE, FrameLayout.SPC, FrameLayout.SPEC, FrameLayout.PCE
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void EncodeFrame_builds_exact_wire_layout(FrameLayout layout)
    {
        byte[] start = [0xAA, 0x55];
        byte[] end = [0x0D, 0x0A];
        byte[] payload = [0x41, 0x42, 0x43];
        var hasS = layout is FrameLayout.SP or FrameLayout.SPE or FrameLayout.SPC or FrameLayout.SPEC;
        var hasE = layout is FrameLayout.PE or FrameLayout.PEC or FrameLayout.SPE or FrameLayout.SPEC or FrameLayout.PCE;
        var hasC = layout is FrameLayout.PC or FrameLayout.PEC or FrameLayout.SPC or FrameLayout.SPEC or FrameLayout.PCE;
        var settings = hasC
            ? SerialProtocolSettings.Crc16(new CrcOptions { OmitTermChar = true }, layout, hasS ? start : null, hasE ? end : null)
            : SerialProtocolSettings.None(layout, hasS ? start : null, hasE ? end : null);
        var checkBytes = hasC ? settings.CreateMessageCheck().Append(payload)[payload.Length..] : [];
        var expected = layout switch
        {
            FrameLayout.P => payload,
            FrameLayout.PE => [.. payload, .. end],
            FrameLayout.PC => [.. payload, .. checkBytes],
            FrameLayout.PEC => [.. payload, .. end, .. checkBytes],
            FrameLayout.SP => [.. start, .. payload],
            FrameLayout.SPE => [.. start, .. payload, .. end],
            FrameLayout.SPC => [.. start, .. payload, .. checkBytes],
            FrameLayout.SPEC => [.. start, .. payload, .. end, .. checkBytes],
            FrameLayout.PCE => [.. payload, .. checkBytes, .. end],
            _ => throw new ArgumentOutOfRangeException()
        };
        Assert.Equal(expected, settings.EncodeFrame(payload));
    }

    [Fact]
    public void Crc_parameter_changes_keep_same_receive_framing_strategy_when_check_does_not_locate_boundary()
    {
        var current = SerialProtocolSettings.Crc16(new CrcOptions { OmitTermChar = true });
        var changed = current with { Polynomial = 0xA001, ExpectedResidue = 0, SuppressInvalidMessages = true };
        Assert.True(current.UsesSameFramingStrategyAs(changed));
    }

    [Fact]
    public void Pce_crc_parameter_changes_change_receive_framing_strategy()
    {
        var current = SerialProtocolSettings.Crc16(new CrcOptions { OmitTermChar = false });
        var changed = current with { Polynomial = 0xA001, ExpectedResidue = 0 };
        Assert.False(current.UsesSameFramingStrategyAs(changed));
    }

    [Fact]
    public void Changing_between_end_and_silence_framing_changes_receive_strategy()
    {
        var terminated = SerialProtocolSettings.Crc16(new CrcOptions { OmitTermChar = false });
        var silence = SerialProtocolSettings.Crc16(new CrcOptions { OmitTermChar = true });
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

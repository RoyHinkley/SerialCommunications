using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class SerialPortTransportConfigurationTests
{
    [Theory]
    [InlineData(SerialHandshake.RequestToSend)]
    [InlineData(SerialHandshake.RequestToSendXOnXOff)]
    public void Rts_handshake_owns_RTS_without_conflicting_manual_configuration(SerialHandshake handshake)
    {
        using var transport = new SerialPortTransport(new SerialPortSettings("TEST", 9600, Handshake: handshake));
        Assert.False(transport.IsOpen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Without_Rts_handshake_manual_RTS_setting_is_preserved(bool enabled)
    {
        using var transport = new SerialPortTransport(new SerialPortSettings("TEST", 9600, RtsEnable: enabled));
        Assert.Equal(enabled, transport.RtsEnable);
    }
}

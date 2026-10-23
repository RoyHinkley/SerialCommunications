using System.IO.Ports;
using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class SerialPortTransportConfigurationTests
{
    [Theory]
    [InlineData(Handshake.RequestToSend)]
    [InlineData(Handshake.RequestToSendXOnXOff)]
    public void Rts_handshake_owns_RTS_without_conflicting_manual_configuration(Handshake handshake)
    {
        var settings = new SerialPortSettings("TEST", 9600, Parity.None, 8, StopBits.One, handshake);

        using var transport = new SerialPortTransport(settings, SerialChannel.RtsModes.Enabled);

        Assert.False(transport.IsOpen);
    }

    [Theory]
    [InlineData(SerialChannel.RtsModes.Enabled, true)]
    [InlineData(SerialChannel.RtsModes.Disabled, false)]
    public void Without_Rts_handshake_manual_RTS_mode_is_preserved(SerialChannel.RtsModes mode, bool expected)
    {
        var settings = new SerialPortSettings("TEST", 9600, Parity.None, 8, StopBits.One, Handshake.None);

        using var transport = new SerialPortTransport(settings, mode);

        Assert.Equal(expected, transport.RtsEnable);
    }

    [Fact]
    public void Toggle_remains_explicitly_unsupported_until_transport_implementation_exists()
    {
        var settings = new SerialPortSettings("TEST", 9600);

        var exception = Assert.Throws<NotSupportedException>(() => new SerialPortTransport(settings, SerialChannel.RtsModes.Toggle));

        Assert.Contains("RTS_CONTROL_TOGGLE", exception.Message, StringComparison.Ordinal);
    }
}

using System.IO.Ports;

namespace SerialCommunications.Serial;

/// <summary>Describes the settings required to open a serial port.</summary>
/// <remarks>
/// This is intentionally only a connection-settings value. Port discovery and lists of UI choices belong
/// outside the serial connection description.
/// </remarks>
/// <param name="PortName">Operating-system serial-port name, such as <c>COM3</c>.</param>
/// <param name="BaudRate">Signaling rate in bits per second.</param>
/// <param name="Parity">Parity scheme applied to each transmitted and received character.</param>
/// <param name="DataBits">Number of data bits in each character.</param>
/// <param name="StopBits">Number of stop bits terminating each character.</param>
/// <param name="Handshake">Hardware or software flow-control mode.</param>
public sealed record SerialPortSettings(
    string PortName,
    int BaudRate = 115200,
    Parity Parity = Parity.None,
    int DataBits = 8,
    StopBits StopBits = StopBits.One,
    Handshake Handshake = Handshake.None);

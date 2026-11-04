namespace SerialCommunications.Serial;

/// <summary>Describes the settings required to open a serial port.</summary>
/// <remarks>
/// This is intentionally only a connection-settings value. Port discovery and lists of UI choices belong
/// outside the serial connection description.
/// </remarks>
/// <param name="PortName">Operating-system serial-port name, such as <c>COM6</c>.</param>
/// <param name="BaudRate">Signaling rate in bits per second.</param>
/// <param name="Parity">Parity scheme applied to each transmitted and received character.</param>
/// <param name="DataBits">Number of data bits in each character.</param>
/// <param name="StopBits">Number of stop bits terminating each character.</param>
/// <param name="Handshake">Hardware or software flow-control mode.</param>
public sealed record SerialPortSettings(
    string PortName,
    int BaudRate = 115200,
    SerialParity Parity = SerialParity.None,
    int DataBits = 8,
    SerialStopBits StopBits = SerialStopBits.One,
    SerialHandshake Handshake = SerialHandshake.None,
    bool RtsEnable = true,
    bool DtrEnable = true);

using System.IO.Ports;
using System.Text;

namespace SerialCommunications.Serial;

/// <summary>
/// Narrow boundary between <see cref="SerialChannel"/> and the OS serial-port implementation.
/// </summary>
/// <remarks>
/// SerialChannel owns the transport instance. Implementations must release their underlying OS resources
/// when disposed. Keeping this boundary small lets lifecycle and recovery behavior be fault-injected in
/// tests without replacing the SerialChannel logic being tested.
/// </remarks>
internal interface ISerialTransport : IDisposable
{
    event SerialDataReceivedEventHandler? DataReceived;
    event SerialPinChangedEventHandler? PinChanged;

    /// <summary>True while the configured endpoint is currently enumerated by the operating system.</summary>
    bool IsAvailable { get; }
    bool IsOpen { get; }
    int BytesToRead { get; }
    bool RtsEnable { get; set; }
    bool DtrEnable { get; set; }
    bool CtsHolding { get; }
    bool DsrHolding { get; }
    bool CDHolding { get; }

    void Open();
    void Close();
    void DiscardOutBuffer();
    void DiscardInBuffer();
    int Read(byte[] buffer, int offset, int count);
    void Write(byte[] buffer, int offset, int count);
}

/// <summary>Creates one independently owned serial transport for an acquisition attempt.</summary>
internal interface ISerialTransportFactory
{
    ISerialTransport Create(SerialPortSettings settings, SerialChannel.RtsModes rtsMode);
}

internal sealed class SerialPortTransportFactory : ISerialTransportFactory
{
    public ISerialTransport Create(SerialPortSettings settings, SerialChannel.RtsModes rtsMode)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SerialPortTransport(settings, rtsMode);
    }
}

internal sealed class SerialPortTransport : ISerialTransport
{
    private static readonly Encoding Ascii8 = Encoding.Latin1;
    private readonly SerialPort port;
    private readonly string portName;

    public SerialPortTransport(SerialPortSettings settings, SerialChannel.RtsModes rtsMode)
    {
        if (rtsMode == SerialChannel.RtsModes.Toggle)
            throw new NotSupportedException("RTS_CONTROL_TOGGLE has not yet been ported to the current .NET serial implementation.");

        portName = settings.PortName;
        port = new SerialPort
        {
            PortName = settings.PortName,
            BaudRate = settings.BaudRate,
            Parity = settings.Parity,
            DataBits = settings.DataBits,
            StopBits = settings.StopBits,
            Handshake = settings.Handshake,
            DiscardNull = false,
            ReceivedBytesThreshold = 1,
            ReadTimeout = 20,
            WriteTimeout = 20,
            Encoding = Ascii8,
            DtrEnable = true
        };

        // When RTS/CTS handshaking is selected, SerialPort owns RTS. Assigning RtsEnable as well is invalid on
        // some implementations and, more importantly, expresses two conflicting owners for the same signal.
        if (settings.Handshake is not Handshake.RequestToSend and not Handshake.RequestToSendXOnXOff)
            port.RtsEnable = rtsMode == SerialChannel.RtsModes.Enabled;
    }

    public event SerialDataReceivedEventHandler? DataReceived
    {
        add => port.DataReceived += value;
        remove => port.DataReceived -= value;
    }

    public event SerialPinChangedEventHandler? PinChanged
    {
        add => port.PinChanged += value;
        remove => port.PinChanged -= value;
    }

    public bool IsAvailable => SerialPort.GetPortNames().Contains(portName, StringComparer.OrdinalIgnoreCase);
    public bool IsOpen => port.IsOpen;
    public int BytesToRead => port.BytesToRead;
    public bool RtsEnable { get => port.RtsEnable; set => port.RtsEnable = value; }
    public bool DtrEnable { get => port.DtrEnable; set => port.DtrEnable = value; }
    public bool CtsHolding => port.CtsHolding;
    public bool DsrHolding => port.DsrHolding;
    public bool CDHolding => port.CDHolding;

    public void Open() => port.Open();
    public void Close() => port.Close();
    public void DiscardOutBuffer() => port.DiscardOutBuffer();
    public void DiscardInBuffer() => port.DiscardInBuffer();
    public int Read(byte[] buffer, int offset, int count) => port.Read(buffer, offset, count);
    public void Write(byte[] buffer, int offset, int count) => port.Write(buffer, offset, count);
    public void Dispose() => port.Dispose();
}

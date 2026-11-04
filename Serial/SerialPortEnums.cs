namespace SerialCommunications.Serial;

/// <summary>Platform-neutral serial character parity (values match System.IO.Ports.Parity).</summary>
public enum SerialParity { None = 0, Odd = 1, Even = 2, Mark = 3, Space = 4 }
/// <summary>Platform-neutral stop-bit setting (values match System.IO.Ports.StopBits).</summary>
public enum SerialStopBits { None = 0, One = 1, Two = 2, OnePointFive = 3 }
/// <summary>Platform-neutral serial flow control (values match System.IO.Ports.Handshake).</summary>
public enum SerialHandshake { None = 0, XOnXOff = 1, RequestToSend = 2, RequestToSendXOnXOff = 3 }

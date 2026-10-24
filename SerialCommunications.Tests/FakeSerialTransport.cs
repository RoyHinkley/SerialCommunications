using System.IO.Ports;
using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

internal sealed class FakeSerialTransportFactory : ISerialTransportFactory
{
    private readonly object sync = new();
    private readonly Queue<FakeSerialTransport> transports = new();

    public List<FakeSerialTransport> Created { get; } = [];

    public FakeSerialTransport Enqueue()
    {
        var transport = new FakeSerialTransport();
        lock (sync) transports.Enqueue(transport);
        return transport;
    }

    public ISerialTransport Create(SerialPortSettings settings, SerialChannel.RtsModes rtsMode)
    {
        lock (sync)
        {
            var transport = transports.Count > 0 ? transports.Dequeue() : new FakeSerialTransport();
            transport.Settings = settings;
            transport.RtsMode = rtsMode;
            Created.Add(transport);
            return transport;
        }
    }
}

internal sealed class FakeSerialTransport : ISerialTransport
{
    private readonly object receiveSync = new();
    private readonly object writeSync = new();
    private readonly Queue<byte> receiveBytes = new();
    private readonly List<byte[]> writes = [];
    private readonly ManualResetEventSlim closed = new(false);
    private SerialDataReceivedEventHandler? dataReceived;
    private SerialPinChangedEventHandler? pinChanged;

    public SerialPortSettings? Settings { get; set; }
    public SerialChannel.RtsModes RtsMode { get; set; }
    public Exception? OpenException { get; set; }
    public Exception? DiscardOutException { get; set; }
    public Exception? DiscardInException { get; set; }
    public Exception? ReadException { get; set; }
    public Exception? WriteException { get; set; }
    public Exception? CloseException { get; set; }
    public Exception? DisposeException { get; set; }
    public bool CloseInterruptsBlockedWrite { get; set; } = true;
    public bool DisposeInterruptsBlockedWrite { get; set; } = true;
    public ManualResetEventSlim OpenEntered { get; } = new(false);
    public ManualResetEventSlim? OpenGate { get; set; }
    public ManualResetEventSlim WriteEntered { get; } = new(false);
    public ManualResetEventSlim WriteExited { get; } = new(false);
    public ManualResetEventSlim? WriteGate { get; set; }

    public int OpenCalls { get; private set; }
    public int ReadCalls { get; private set; }
    public int WriteCalls { get; private set; }
    public int CloseCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public int DataReceivedSubscriptions { get; private set; }
    public int PinChangedSubscriptions { get; private set; }
    public bool IsAvailable { get; set; } = true;
    public bool IsOpen { get; private set; }
    public int BytesToRead { get { lock (receiveSync) return receiveBytes.Count; } }
    public IReadOnlyList<byte[]> Writes { get { lock (writeSync) return writes.Select(w => w.ToArray()).ToArray(); } }
    public bool RtsEnable { get; set; }
    public bool DtrEnable { get; set; }
    public bool CtsHolding { get; set; }
    public bool DsrHolding { get; set; }
    public bool CDHolding { get; set; }

    public event SerialDataReceivedEventHandler? DataReceived
    {
        add { dataReceived += value; DataReceivedSubscriptions++; }
        remove { dataReceived -= value; DataReceivedSubscriptions--; }
    }

    public event SerialPinChangedEventHandler? PinChanged
    {
        add { pinChanged += value; PinChangedSubscriptions++; }
        remove { pinChanged -= value; DataReceivedSubscriptions--; }
    }

    public void EnqueueReceived(params byte[] bytes)
    {
        lock (receiveSync)
            foreach (var value in bytes) receiveBytes.Enqueue(value);
    }

    public void RaiseDataReceived() => dataReceived?.Invoke(this, null!);

    public void Open()
    {
        OpenCalls++;
        OpenEntered.Set();
        OpenGate?.Wait();
        if (OpenException is not null) throw OpenException;
        closed.Reset();
        IsOpen = true;
    }

    public void Close()
    {
        CloseCalls++;
        IsOpen = false;
        if (CloseInterruptsBlockedWrite) closed.Set();
        if (CloseException is not null) throw CloseException;
    }

    public void DiscardOutBuffer()
    {
        if (DiscardOutException is not null) throw DiscardOutException;
    }

    public void DiscardInBuffer()
    {
        if (DiscardInException is not null) throw DiscardInException;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        ReadCalls++;
        if (ReadException is not null) throw ReadException;
        lock (receiveSync)
        {
            var read = Math.Min(count, receiveBytes.Count);
            for (var i = 0; i < read; i++) buffer[offset + i] = receiveBytes.Dequeue();
            return read;
        }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        WriteCalls++;
        WriteEntered.Set();
        try
        {
            if (WriteGate is not null)
            {
                var releasedBy = WaitHandle.WaitAny([WriteGate.WaitHandle, closed.WaitHandle]);
                if (releasedBy == 1) throw new IOException("transport closed while write was blocked");
            }
            if (WriteException is not null) throw WriteException;
            if (!IsOpen) throw new IOException("transport is closed");
            var written = new byte[count];
            Array.Copy(buffer, offset, written, 0, count);
            lock (writeSync) writes.Add(written);
        }
        finally { WriteExited.Set(); }
    }

    public void Dispose()
    {
        DisposeCalls++;
        IsOpen = false;
        if (DisposeInterruptsBlockedWrite) closed.Set();
        if (DisposeException is not null) throw DisposeException;
    }
}
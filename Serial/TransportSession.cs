using System.IO.Ports;

namespace SerialCommunications.Serial;

/// <summary>Owns one transport through acquisition, mediation, and shutdown.</summary>
/// <remarks>
/// Quarantine is terminal. The session reports it once and does not attempt recovery.
/// The channel owns the decision to create a replacement session.
/// </remarks>
internal sealed class TransportSession : ISerialTransport
{
    internal enum Phase { Acquisition, Mediation, Shutdown }
    internal enum OperationResult { Succeeded, Rejected, Failed, TimedOut, Quarantined }

    private readonly ISerialTransport transport;
    private int phase = (int)Phase.Acquisition;
    private int quarantined;
    private int shutdownStarted;
    private int notificationsEnabled = 1;

    public event Action<TransportSession, string>? Quarantined;
    public event SerialDataReceivedEventHandler? DataReceived;
    public event SerialPinChangedEventHandler? PinChanged;

    public TransportSession(ISerialTransport transport)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        transport.DataReceived += ForwardDataReceived;
        transport.PinChanged += ForwardPinChanged;
    }

    public Phase CurrentPhase => (Phase)Volatile.Read(ref phase);
    public bool IsQuarantined => Volatile.Read(ref quarantined) != 0;
    private bool CanMediate => !IsQuarantined && CurrentPhase == Phase.Mediation;

    private void ForwardDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (CanMediate && Volatile.Read(ref notificationsEnabled) != 0)
            DataReceived?.Invoke(this, e);
    }

    private void ForwardPinChanged(object sender, SerialPinChangedEventArgs e)
    {
        if (CanMediate && Volatile.Read(ref notificationsEnabled) != 0)
            PinChanged?.Invoke(this, e);
    }

    public void Quarantine(string reason)
    {
        if (Interlocked.Exchange(ref quarantined, 1) != 0) return;
        Volatile.Write(ref notificationsEnabled, 0);
        Quarantined?.Invoke(this, reason);
    }

    /// <summary>Runs the existing acquisition sequence on its caller's acquisition worker.</summary>
    public OperationResult Acquire()
    {
        if (IsQuarantined) return OperationResult.Quarantined;
        if (CurrentPhase != Phase.Acquisition) return OperationResult.Rejected;
        transport.Open();
        transport.DiscardOutBuffer();
        transport.DiscardInBuffer();
        if (IsQuarantined) return OperationResult.Quarantined;
        Volatile.Write(ref phase, (int)Phase.Mediation);
        return OperationResult.Succeeded;
    }

    /// <summary>Existing guarded release, moved from SerialChannel. No quarantine cleanup is attempted.</summary>
    public OperationResult Shutdown(TimeSpan timeout, Action<string> reportError)
    {
        ArgumentNullException.ThrowIfNull(reportError);
        if (Interlocked.Exchange(ref shutdownStarted, 1) != 0) return OperationResult.Rejected;
        Volatile.Write(ref phase, (int)Phase.Shutdown);
        Volatile.Write(ref notificationsEnabled, 0);
        try { transport.DataReceived -= ForwardDataReceived; }
        catch (Exception e) { reportError($"DataReceived unsubscribe exception: {e}"); }
        try { transport.PinChanged -= ForwardPinChanged; }
        catch (Exception e) { reportError($"PinChanged unsubscribe exception: {e}"); }

        var release = ExternalCallGuard.Execute(() =>
        {
            try { transport.Close(); }
            finally { transport.Dispose(); }
        }, timeout, "serial_transport_release");

        if (release.Status == ExternalCallGuard.Outcome.TimedOut)
        {
            Quarantine($"transport release timed out after {timeout.TotalSeconds:0.###} seconds; OS handle release is unconfirmed.");
            return OperationResult.TimedOut;
        }
        if (release.Status == ExternalCallGuard.Outcome.Faulted)
        {
            reportError($"serial transport release exception: {release.Exception}");
            return OperationResult.Failed;
        }
        return OperationResult.Succeeded;
    }

    public bool IsAvailable => CanMediate && transport.IsAvailable;
    public bool IsOpen => CanMediate && transport.IsOpen;
    public int BytesToRead => CanMediate ? transport.BytesToRead : 0;
    public bool RtsEnable
    {
        get => CanMediate && transport.RtsEnable;
        set { if (CanMediate) transport.RtsEnable = value; }
    }
    public bool DtrEnable
    {
        get => CanMediate && transport.DtrEnable;
        set { if (CanMediate) transport.DtrEnable = value; }
    }
    public bool CtsHolding => CanMediate && transport.CtsHolding;
    public bool DsrHolding => CanMediate && transport.DsrHolding;
    public bool CDHolding => CanMediate && transport.CDHolding;

    // The interface is retained for compatibility with the existing receive/transmit workers.
    // The channel uses Acquire() and Shutdown() for lifecycle transitions.
    public void Open() { if (!IsQuarantined) throw new InvalidOperationException("Use Acquire()."); }
    public void Close() { if (!IsQuarantined) throw new InvalidOperationException("Use Shutdown()."); }
    public void Dispose() { /* Shutdown owns physical release. */ }
    public void DiscardOutBuffer() { if (CanMediate) transport.DiscardOutBuffer(); }
    public void DiscardInBuffer() { if (CanMediate) transport.DiscardInBuffer(); }
    public int Read(byte[] buffer, int offset, int count) =>
        CanMediate ? transport.Read(buffer, offset, count) : 0;
    public void Write(byte[] buffer, int offset, int count)
    {
        if (CanMediate) transport.Write(buffer, offset, count);
    }
}

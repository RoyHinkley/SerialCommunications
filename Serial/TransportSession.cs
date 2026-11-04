using System.IO.Ports;

namespace SerialCommunications.Serial;

/// <summary>Owns one transport through acquisition, mediation, and shutdown.</summary>
/// <remarks>
/// Quarantine is terminal. The session reports it once and does not attempt recovery.
/// The channel owns the decision to create a replacement session.
/// </remarks>
internal sealed class TransportSession
{
    internal enum Phase { Acquisition, Mediation, Shutdown, Closed }
    internal enum OperationResult { Succeeded, Rejected, Failed, TimedOut, Quarantined }

    /// <summary>The most recent underlying operation exception, if any.</summary>
    public Exception? LastFailure { get; private set; }

    private OperationResult Execute(Action operation)
    {
        if (IsQuarantined) return OperationResult.Quarantined;
        if (!CanMediate || transport is null) return OperationResult.Rejected;
        try
        {
            operation();
            return IsQuarantined ? OperationResult.Quarantined : OperationResult.Succeeded;
        }
        catch (Exception e)
        {
            LastFailure = e;
            return OperationResult.Failed;
        }
    }

    private OperationResult Query<T>(Func<T> query, out T value)
    {
        value = default!;
        if (IsQuarantined) return OperationResult.Quarantined;
        if (!CanMediate) return OperationResult.Rejected;
        try
        {
            var result = query();
            if (IsQuarantined) return OperationResult.Quarantined;
            value = result;
            return OperationResult.Succeeded;
        }
        catch (Exception e)
        {
            LastFailure = e;
            return OperationResult.Failed;
        }
    }

    public OperationResult GetAvailability(out bool value) => Query(() => transport.IsAvailable, out value);
    public OperationResult GetOpen(out bool value) => Query(() => transport.IsOpen, out value);
    public OperationResult GetBytesToRead(out int value) => Query(() => transport.BytesToRead, out value);
    public OperationResult GetRts(out bool value) => Query(() => transport.RtsEnable, out value);
    public OperationResult SetRts(bool value) => Execute(() => transport.RtsEnable = value);
    public OperationResult GetDtr(out bool value) => Query(() => transport.DtrEnable, out value);
    public OperationResult SetDtr(bool value) => Execute(() => transport.DtrEnable = value);
    public OperationResult GetCts(out bool value) => Query(() => transport.CtsHolding, out value);
    public OperationResult GetDsr(out bool value) => Query(() => transport.DsrHolding, out value);
    public OperationResult GetDcd(out bool value) => Query(() => transport.CDHolding, out value);
    public OperationResult ReadBytes(byte[] buffer, int offset, int count, out int bytesRead) =>
        Query(() => transport.Read(buffer, offset, count), out bytesRead);
    public OperationResult WriteBytes(byte[] buffer, int offset, int count) =>
        Execute(() => transport.Write(buffer, offset, count));


    private readonly ISerialTransportFactory factory;
    private readonly SerialPortSettings settings;
    private ISerialTransport? transport;
    private int phase = (int)Phase.Acquisition;
    private int quarantined;
    private int shutdownStarted;
    private int notificationsEnabled = 1;

    public event Action<TransportSession, string>? Quarantined;
    public event SerialDataReceivedEventHandler? DataReceived;
    public event SerialPinChangedEventHandler? PinChanged;

    public TransportSession(ISerialTransportFactory factory, SerialPortSettings settings)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public Phase CurrentPhase => (Phase)Volatile.Read(ref phase);
    public bool IsQuarantined => Volatile.Read(ref quarantined) != 0;
    private bool CanMediate => !IsQuarantined && CurrentPhase == Phase.Mediation && transport is not null;

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
        if (CurrentPhase != Phase.Acquisition && CurrentPhase != Phase.Closed) return OperationResult.Rejected;
        if (transport is not null) return OperationResult.Rejected;
        Volatile.Write(ref phase, (int)Phase.Acquisition);
        Volatile.Write(ref shutdownStarted, 0);
        Volatile.Write(ref notificationsEnabled, 1);
        try
        {
            var created = factory.Create(settings);
            transport = created;
            created.DataReceived += ForwardDataReceived;
            created.PinChanged += ForwardPinChanged;
            created.Open();
            transport.DiscardOutBuffer();
            transport.DiscardInBuffer();
            if (IsQuarantined) return OperationResult.Quarantined;
            Volatile.Write(ref phase, (int)Phase.Mediation);
            return OperationResult.Succeeded;
        }
        catch (Exception e)
        {
            LastFailure = e;
            return OperationResult.Failed;
        }
    }

    /// <summary>Existing guarded release, moved from SerialChannel. No quarantine cleanup is attempted.</summary>
    public OperationResult Shutdown(TimeSpan timeout, Action<string> reportError)
    {
        ArgumentNullException.ThrowIfNull(reportError);
        if (Interlocked.Exchange(ref shutdownStarted, 1) != 0) return OperationResult.Rejected;
        Volatile.Write(ref phase, (int)Phase.Shutdown);
        Volatile.Write(ref notificationsEnabled, 0);
        var retiring = transport;
        if (retiring is null) { Volatile.Write(ref phase, (int)Phase.Closed); return OperationResult.Succeeded; }
        try { retiring.DataReceived -= ForwardDataReceived; }
        catch (Exception e) { reportError($"DataReceived unsubscribe exception: {e}"); }
        try { retiring.PinChanged -= ForwardPinChanged; }
        catch (Exception e) { reportError($"PinChanged unsubscribe exception: {e}"); }

        var release = ExternalCallGuard.Execute(() =>
        {
            try { retiring.Close(); }
            finally { retiring.Dispose(); }
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
        transport = null;
        Volatile.Write(ref phase, (int)Phase.Closed);
        return OperationResult.Succeeded;
    }

}

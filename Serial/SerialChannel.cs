using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using SerialCommunications.Serial.Protocol;

namespace SerialCommunications.Serial;

public readonly record struct SerialSignalState(bool Rts, bool Cts, bool Dtr, bool Dsr, bool Dcd, bool Ring)
{
    public override string ToString() => $"RTS={(Rts ? 1 : 0)} CTS={(Cts ? 1 : 0)} DTR={(Dtr ? 1 : 0)} DSR={(Dsr ? 1 : 0)} DCD={(Dcd ? 1 : 0)} RI={(Ring ? 1 : 0)}";
}

/// <summary>Coordinates serial transport ownership, message processing, pacing, recovery, and diagnostics.</summary>
public sealed class SerialChannel : IDisposable
{
    private static readonly TimeSpan AvailabilityPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WorkerJoinTimeout = TimeSpan.FromMilliseconds(250);
    private static long instanceCount;
    public enum RtsModes { Enabled, Disabled, Toggle }

    public SerialPortSettings PortSettings { get; set; }
    public RtsModes RtsMode { get; set; } = RtsModes.Enabled;
    public int MillisecondsBetweenMessages { get; set; } = -1;
    public int MillisecondsBetweenBytes { get; set; } = -1;
    public int MaximumMillisecondsSilenceInMessage { get; set; } = 5;
    public SerialRecoveryPolicy RecoveryPolicy { get; set; } = SerialRecoveryPolicy.Default;
    public SerialChannelTelemetry Telemetry { get; } = new();

    public event EventHandler? Connected;
    public event EventHandler? Disconnecting;
    public event Action<ReceivedData>? DataReceived;
    public event Action<SerialSignalState>? SignalsChanged;
    public event Action<string, TraceFlags>? Trace;
    public event Action<string>? Error;

    public double MillisecondsSinceLastTx => txSession?.Stopwatch.Elapsed.TotalMilliseconds ?? 0;
    public double MillisecondsSinceLastRx => rxSw.Elapsed.TotalMilliseconds;
    public bool ConnectionRequested { get; private set; }
    public SerialConnectionState ConnectionState { get; private set; } = SerialConnectionState.Disconnected;
    public bool RecoveryActive => recoveryActive && ConnectionRequested && ConnectionState == SerialConnectionState.Unavailable;
    public int RecoveryAttempts { get; private set; }
    public DateTimeOffset? RecoveryStartedAt { get; private set; }
    public bool Ready => ConnectionState == SerialConnectionState.Connected && port?.IsOpen == true;
    public bool HaveWork => txSession?.HaveWork == true;
    public bool Busy => Ready && HaveWork;
    public bool Idle => !Busy;
    public bool Free => Ready && !HaveWork;
    public SerialProtocolSettings ProtocolSettings => protocolSettings;
    public SerialProtocolSettings? PendingProtocolSettings => pendingProtocolSettings;

    private readonly long instanceNumber = Interlocked.Increment(ref instanceCount);
    private readonly ISerialTransportFactory transportFactory;
    private readonly object lifecycleLock = new();
    private readonly object protocolLock = new();
    private readonly ConcurrentQueue<SerialProtocolSettings> receivedSettings = new();
    private ISerialTransport? port;
    private SerialMessagePipeline? receivePipeline;
    private volatile bool active;
    private bool disposed;
    private TxSession? txSession;
    private AcquisitionAttempt? acquisitionAttempt;
    private Thread? recoveryThread;
    private volatile bool recoveryActive;
    private readonly AutoResetEvent recoverySignal = new(false);
    private readonly Stopwatch rxSw = new(), recoverySw = new();
    private SerialSignalState? lastSignalState;
    private SerialProtocolSettings protocolSettings = SerialProtocolSettings.None();
    private SerialProtocolSettings? pendingProtocolSettings;
    private string? recoveryFailureSignature;
    private int repeatedRecoveryFailures;

    private sealed class TxSession(ISerialTransport transport, IMessageCheck check) : IDisposable
    {
        private int active = 1;
        private int transmitting;
        private IMessageCheck check = check;
        public ISerialTransport Transport { get; } = transport;
        public IMessageCheck Check => Volatile.Read(ref check);
        public ConcurrentQueue<byte[]> Queue { get; } = new();
        public AutoResetEvent Signal { get; } = new(false);
        public Stopwatch Stopwatch { get; } = new();
        public Thread? Thread { get; set; }
        public bool Active => Volatile.Read(ref active) != 0;
        public bool HaveWork => Volatile.Read(ref transmitting) != 0 || !Queue.IsEmpty;
        public void SetCheck(IMessageCheck value) => Volatile.Write(ref check, value);
        public void SetTransmitting(bool value) => Volatile.Write(ref transmitting, value ? 1 : 0);
        public void Stop() { Interlocked.Exchange(ref active, 0); Signal.Set(); }
        public void Dispose() => Signal.Dispose();
    }

    private sealed class AcquisitionAttempt(ISerialTransport transport, bool recovery)
    {
        private int abandoned;
        private int published;
        public ISerialTransport Transport { get; } = transport;
        public bool Recovery { get; } = recovery;
        public ManualResetEventSlim Completion { get; } = new(false);
        public bool Abandoned => Volatile.Read(ref abandoned) != 0;
        public bool Published => Volatile.Read(ref published) != 0;
        public void Abandon() { Interlocked.Exchange(ref abandoned, 1); Completion.Set(); }
        public void Complete(bool wasPublished)
        {
            if (wasPublished) Interlocked.Exchange(ref published, 1);
            Completion.Set();
        }
    }

    public SerialChannel(SerialPortSettings portSettings) : this(portSettings, new SerialPortTransportFactory()) { }
    public SerialChannel(SerialChannelConfiguration configuration) : this(configuration, new SerialPortTransportFactory()) { }
    internal SerialChannel(SerialPortSettings portSettings, ISerialTransportFactory transportFactory)
    {
        PortSettings = portSettings ?? throw new ArgumentNullException(nameof(portSettings));
        this.transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
    }
    internal SerialChannel(SerialChannelConfiguration configuration, ISerialTransportFactory transportFactory)
        : this((configuration ?? throw new ArgumentNullException(nameof(configuration))).PortSettings, transportFactory)
    {
        RtsMode = configuration.RtsMode;
        MillisecondsBetweenMessages = configuration.MillisecondsBetweenMessages;
        MillisecondsBetweenBytes = configuration.MillisecondsBetweenBytes;
        MaximumMillisecondsSilenceInMessage = configuration.MaximumMillisecondsSilenceInMessage;
        RecoveryPolicy = configuration.RecoveryPolicy;
        ApplyProtocolSettings(configuration.ProtocolSettings);
    }
    public SerialChannel(string portName, int baudRate = 115200) : this(new SerialPortSettings(portName, baudRate)) { }

    public bool Command(ReadOnlySpan<byte> command)
    {
        ThrowIfDisposed();
        if (command.IsEmpty) return false;
        var session = txSession;
        if (session is null || !session.Active) return false;
        var bytes = command.ToArray();
        ReportTrace($"received Command \"{BinaryDataFormatter.ToEscapedText(bytes)}\"", TraceFlags.Detail);
        session.Queue.Enqueue(bytes);
        session.Signal.Set();
        return Ready;
    }

    public bool StageProtocolSettings(SerialProtocolSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (lifecycleLock)
        {
            ThrowIfDisposed();
            SerialMessagePipeline? pipeline;
            lock (protocolLock)
            {
                pendingProtocolSettings = settings;
                pipeline = receivePipeline;
                if (pipeline is null) { ApplyProtocolSettings(settings); pendingProtocolSettings = null; return true; }
            }
            pipeline.StageDecoder(settings.CreateMessageDecoder(), () => ApplyStagedProtocolSettings(settings));
        }
        return true;
    }

    public bool Connect()
    {
        AcquisitionAttempt? attempt;
        lock (lifecycleLock)
        {
            ThrowIfDisposed();
            if (ConnectionState == SerialConnectionState.Connecting || acquisitionAttempt is not null)
            {
                ReportError("Connect requested while the channel is already connecting.");
                return false;
            }
            if (ConnectionState == SerialConnectionState.Connected)
            {
                ReportError("Connect requested while the channel is already connected.");
                return false;
            }
            ConnectionRequested = true;
            attempt = StartAcquisition(false);
            StartConnectionMaintenance();
        }
        if (attempt is null) return false;
        attempt.Completion.Wait();
        return attempt.Published;
    }

    private AcquisitionAttempt? StartAcquisition(bool recovery)
    {
        ConnectionState = SerialConnectionState.Connecting;
        if (!recovery || repeatedRecoveryFailures < 3)
            ReportTrace(recovery ? "recovery connection attempt..." : "connecting...", recovery ? TraceFlags.Detail : TraceFlags.None);
        try
        {
            var attempt = new AcquisitionAttempt(transportFactory.Create(PortSettings, RtsMode), recovery);
            acquisitionAttempt = attempt;
            _ = StartThread(() => AcquireTransport(attempt), "transport_acquisition");
            return attempt;
        }
        catch (Exception e)
        {
            if (recovery) RecordRecoveryFailure(e); else ReportError($"Connect failed: {e}");
            ConnectionState = SerialConnectionState.Unavailable;
            if (!recovery) BeginRecoveryPeriod();
            return null;
        }
    }

    private void AcquireTransport(AcquisitionAttempt attempt)
    {
        Exception? failure = null;
        var subscribed = false;
        try
        {
            attempt.Transport.DataReceived += RxDetected;
            attempt.Transport.PinChanged += PinChanged;
            subscribed = true;
            attempt.Transport.Open();
            attempt.Transport.DiscardOutBuffer();
            attempt.Transport.DiscardInBuffer();
        }
        catch (Exception e) { failure = e; }

        var published = false;
        lock (lifecycleLock)
        {
            var current = ReferenceEquals(acquisitionAttempt, attempt);
            if (current) acquisitionAttempt = null;
            if (failure is null && current && !attempt.Abandoned && !disposed && ConnectionRequested)
            {
                port = attempt.Transport;
                pendingProtocolSettings = null;
                CreateCommsSession();
                active = true;
                var session = txSession!;
                session.Thread = StartThread(() => Transmit(session), "transmit");
                ConnectionState = SerialConnectionState.Connected;
                if (attempt.Recovery)
                {
                    Telemetry.RecordSuccessfulRecovery();
                    ReportTrace($"connection recovered after {RecoveryAttempts} attempt{(RecoveryAttempts == 1 ? "" : "s")} and {recoverySw.Elapsed.TotalSeconds:0.0} s.");
                    recoveryFailureSignature = null;
                    repeatedRecoveryFailures = 0;
                }
                else ReportTrace("connected.");
                PublishSignals(true);
                Dispatch(Connected, nameof(Connected));
                published = true;
            }
            else if (failure is not null && current && !attempt.Abandoned && !disposed && ConnectionRequested)
            {
                if (attempt.Recovery) RecordRecoveryFailure(failure); else ReportError($"Connect failed: {failure}");
                ConnectionState = SerialConnectionState.Unavailable;
                if (!attempt.Recovery) BeginRecoveryPeriod();
            }
        }

        if (!published) ReleaseAcquisitionTransport(attempt.Transport, subscribed);
        attempt.Complete(published);
    }

    private void ReleaseAcquisitionTransport(ISerialTransport transport, bool subscribed)
    {
        if (subscribed)
        {
            try { transport.DataReceived -= RxDetected; } catch (Exception e) { ReportError($"DataReceived unsubscribe exception: {e}"); }
            try { transport.PinChanged -= PinChanged; } catch (Exception e) { ReportError($"PinChanged unsubscribe exception: {e}"); }
        }
        try { transport.Close(); } catch (Exception e) { ReportError($"port close exception: {e}"); }
        try { transport.Dispose(); } catch (Exception e) { ReportError($"port dispose exception: {e}"); }
    }

    private void AbandonAcquisition()
    {
        var attempt = acquisitionAttempt;
        acquisitionAttempt = null;
        attempt?.Abandon();
    }

    public bool Disconnect()
    {
        Thread? maintenance;
        lock (lifecycleLock)
        {
            if (disposed) return true;
            ConnectionRequested = false;
            AbandonAcquisition();
            maintenance = StopConnectionMaintenance();
        }
        JoinWorker(maintenance);

        lock (lifecycleLock)
        {
            if (disposed) return true;
            if (ConnectionState == SerialConnectionState.Disconnected && port is null && !WorkersAlive) return true;
            var wasConnected = ConnectionState == SerialConnectionState.Connected;
            ConnectionState = SerialConnectionState.Disconnecting;
            ReportTrace("disconnecting...");
            if (wasConnected) Dispatch(Disconnecting, nameof(Disconnecting));
            var success = StopWorkersAndReleaseTransport();
            lastSignalState = null;
            ConnectionState = SerialConnectionState.Disconnected;
            ReportTrace("disconnected.");
            return success;
        }
    }

    public void WaitForIdle() { ThrowIfDisposed(); while (!Idle) Thread.Sleep(5); }

    private void StartConnectionMaintenance()
    {
        if (recoveryThread?.IsAlive == true) { recoverySignal.Set(); return; }
        recoveryActive = true;
        recoveryThread = StartThread(MaintainConnection, "connection_maintenance");
    }
    private Thread? StopConnectionMaintenance()
    {
        recoveryActive = false;
        recoverySignal.Set();
        var thread = recoveryThread;
        recoveryThread = null;
        return thread;
    }
    private void BeginRecoveryPeriod()
    {
        RecoveryAttempts = 0;
        RecoveryStartedAt = DateTimeOffset.Now;
        recoverySw.Restart();
        recoveryFailureSignature = null;
        repeatedRecoveryFailures = 0;
    }

    private void MaintainConnection()
    {
        ReportTrace("starting connection maintenance thread.", TraceFlags.Detail);
        try
        {
            while (recoveryActive)
            {
                var delay = ConnectionState == SerialConnectionState.Unavailable ? RecoveryPolicy.RetryInterval : AvailabilityPollInterval;
                if (delay <= TimeSpan.Zero) { ReportError("recovery stopped: RetryInterval must be greater than zero."); recoveryActive = false; break; }
                recoverySignal.WaitOne(delay);
                if (!recoveryActive) break;

                AcquisitionAttempt? attempt = null;
                lock (lifecycleLock)
                {
                    if (disposed || !ConnectionRequested) break;
                    if (ConnectionState == SerialConnectionState.Connected)
                    {
                        bool available;
                        try { available = port?.IsAvailable == true; }
                        catch (Exception e) { ReportError($"transport availability check exception: {e.Message}"); continue; }
                        if (!available) HandleTransportUnavailable();
                        continue;
                    }
                    if (ConnectionState != SerialConnectionState.Unavailable) continue;
                    SerialRecoveryPolicy policy;
                    try
                    {
                        policy = RecoveryPolicy;
                        if (!policy.AllowsAttempt(RecoveryAttempts, recoverySw.Elapsed))
                        {
                            ReportTrace($"automatic recovery stopped after {RecoveryAttempts} attempt{(RecoveryAttempts == 1 ? "" : "s")} and {recoverySw.Elapsed.TotalSeconds:0.0} s; connection remains requested and unavailable.");
                            recoveryActive = false;
                            break;
                        }
                    }
                    catch (Exception e) { ReportError($"recovery policy invalid: {e.Message}"); recoveryActive = false; break; }
                    RecoveryAttempts++;
                    Telemetry.RecordRecoveryAttempt();
                    attempt = StartAcquisition(true);
                }
                attempt?.Completion.Wait();
            }
        }
        catch (ObjectDisposedException) when (disposed) { }
        catch (Exception e) { ReportError($"connection maintenance exception: {e}"); }
        finally { ReportTrace("ending connection maintenance thread.", TraceFlags.Detail); }
    }

    private void HandleTransportUnavailable()
    {
        ReportTrace("transport unavailable; preserving connection request and releasing transport.");
        StopWorkersAndReleaseTransport();
        lastSignalState = null;
        ConnectionState = SerialConnectionState.Unavailable;
        BeginRecoveryPeriod();
        Dispatch(Disconnecting, nameof(Disconnecting));
    }

    private void RecordRecoveryFailure(Exception e)
    {
        var signature = $"{e.GetType().FullName}: {e.Message}";
        if (!StringComparer.Ordinal.Equals(signature, recoveryFailureSignature))
        {
            recoveryFailureSignature = signature; repeatedRecoveryFailures = 1;
            ReportError($"recovery attempt {RecoveryAttempts} failed: {signature}"); return;
        }
        repeatedRecoveryFailures++;
        if (repeatedRecoveryFailures <= 3) ReportError($"recovery attempt {RecoveryAttempts} failed: {signature}");
        else if (repeatedRecoveryFailures == 4) ReportError("continuing recovery attempts despite 4 identical failures; further identical failures will be suppressed until the result changes.");
    }

    private void CreateCommsSession()
    {
        txSession = new TxSession(port!, protocolSettings.CreateMessageCheck());
        lastSignalState = null;
        rxSw.Reset();
        while (receivedSettings.TryDequeue(out _)) { }
        receivePipeline = new SerialMessagePipeline(
            port!, protocolSettings.CreateMessageDecoder(), HandleDecodedMessage,
            () => TimeSpan.FromMilliseconds(Math.Max(1, MaximumMillisecondsSilenceInMessage)),
            e => ReportError($"receive exception: {e}"),
            e => ReportError($"receive consumer exception: {e}"),
            OnBytesAcquired,
            _ => receivedSettings.Enqueue(protocolSettings));
    }

    private Thread StartThread(ThreadStart action, string role)
    {
        var thread = new Thread(action) { Name = $"SerialChannel {instanceNumber} {role}", IsBackground = true };
        thread.Start(); return thread;
    }

    private bool StopWorkersAndReleaseTransport()
    {
        active = false;
        var session = txSession;
        txSession = null;
        session?.Stop();

        var pipeline = receivePipeline;
        receivePipeline = null;
        pipeline?.Dispose();

        var p = port;
        port = null;
        var success = true;
        if (p is not null)
        {
            try { p.DataReceived -= RxDetected; } catch (Exception e) { success = false; ReportError($"DataReceived unsubscribe exception: {e}"); }
            try { p.PinChanged -= PinChanged; } catch (Exception e) { success = false; ReportError($"PinChanged unsubscribe exception: {e}"); }
            try { p.Close(); } catch (Exception e) { success = false; ReportError($"port close exception: {e}"); }
            try { p.Dispose(); } catch (Exception e) { success = false; ReportError($"port dispose exception: {e}"); }
        }

        if (session is not null)
        {
            var joined = JoinWorker(session.Thread, WorkerJoinTimeout);
            if (!joined)
            {
                success = false;
                ReportError($"transmit worker did not stop within {WorkerJoinTimeout.TotalMilliseconds:0} ms; abandoning isolated old session.");
            }
        }
        lock (protocolLock) pendingProtocolSettings = null;
        while (receivedSettings.TryDequeue(out _)) { }
        return success;
    }

    private static void JoinWorker(Thread? thread) { if (thread is not null && thread != Thread.CurrentThread && thread.IsAlive) thread.Join(); }
    private static bool JoinWorker(Thread? thread, TimeSpan timeout) => thread is null || thread == Thread.CurrentThread || !thread.IsAlive || thread.Join(timeout);
    private bool WorkersAlive => txSession?.Thread?.IsAlive == true || receivePipeline is not null;

    private void Transmit(TxSession session)
    {
        ReportTrace("starting Transmit thread.", TraceFlags.Detail);
        try
        {
            byte[] tx = [];
            var offset = 0;
            var millisecondsBetweenMessages = -1;
            var millisecondsBetweenBytes = -1;
            session.Stopwatch.Restart();
            while (session.Active)
            {
                if (offset < tx.Length)
                {
                    try
                    {
                        if (millisecondsBetweenBytes < 0)
                        {
                            if (session.Stopwatch.Elapsed.TotalMilliseconds >= millisecondsBetweenMessages)
                            {
                                session.Transport.Write(tx, 0, tx.Length);
                                if (!session.Active) break;
                                session.Stopwatch.Restart(); offset = tx.Length;
                                Telemetry.RecordMessageTransmitted();
                                ReportTrace($"Transmit: {BinaryDataFormatter.ToHexBytes(tx)}");
                            }
                            else Thread.Sleep(1);
                        }
                        else if (session.Stopwatch.Elapsed.TotalMilliseconds >= millisecondsBetweenBytes)
                        {
                            ReportTrace($"Transmit byte ({session.Stopwatch.Elapsed.TotalMilliseconds:0} ms since last): {tx[offset]:X2}", TraceFlags.Detail);
                            session.Transport.Write(tx, offset++, 1);
                            if (!session.Active) break;
                            session.Stopwatch.Restart();
                            if (offset == tx.Length)
                            {
                                Telemetry.RecordMessageTransmitted();
                                ReportTrace($"Transmit: {BinaryDataFormatter.ToHexBytes(tx)}");
                            }
                        }
                        else Thread.Sleep(1);
                    }
                    catch (Exception e)
                    {
                        if (!session.Active) break;
                        ReportError($"transmit exception: {e}");
                        Thread.Sleep(Math.Max(20, millisecondsBetweenMessages));
                    }
                }
                else
                {
                    session.SetTransmitting(false);
                    if (session.Queue.TryDequeue(out var command))
                    {
                        millisecondsBetweenMessages = MillisecondsBetweenMessages;
                        millisecondsBetweenBytes = MillisecondsBetweenBytes;
                        offset = 0; tx = session.Check.Append(command); session.SetTransmitting(true);
                    }
                    else session.Signal.WaitOne(1000);
                }
            }
        }
        catch (Exception e) { if (session.Active) ReportError($"fatal Transmit exception: {e}"); }
        finally
        {
            session.SetTransmitting(false);
            session.Dispose();
            ReportTrace("ending Transmit thread.", TraceFlags.Detail);
        }
    }

    private void OnBytesAcquired(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        Telemetry.RecordBytesReceived(bytes.Length);
        rxSw.Restart();
        ReportTrace($"Acquired: {BinaryDataFormatter.ToHexBytes(bytes.Span)}", TraceFlags.Detail);
    }

    private void HandleDecodedMessage(DecodedMessage message)
    {
        if (!receivedSettings.TryDequeue(out var settings)) settings = protocolSettings;
        var data = ReceivedDataAdapter.Create(message, settings);
        Telemetry.RecordMessageReceived();
        if (settings.CheckKind != MessageCheckKind.None && !message.CheckValid)
        {
            Telemetry.RecordCheckFailure();
            ReportError($"Receive check failed: {BinaryDataFormatter.ToEscapedText(data.WireBytes.Span)}");
            if (settings.SuppressInvalidMessages) return;
        }
        ReportTrace($"Receive message: {BinaryDataFormatter.ToEscapedText(data.PayloadBytes.Span)}");
        DispatchData(data);
    }

    private void ApplyStagedProtocolSettings(SerialProtocolSettings settings)
    {
        lock (protocolLock)
        {
            if (pendingProtocolSettings != settings) return;
            ApplyProtocolSettings(settings); pendingProtocolSettings = null;
        }
        ReportTrace("applied staged protocol settings at message boundary.", TraceFlags.Detail);
    }
    private void ApplyProtocolSettings(SerialProtocolSettings settings)
    {
        protocolSettings = settings;
        txSession?.SetCheck(settings.CreateMessageCheck());
    }

    internal ReceivedData DecodeSilenceFramedMessage(ReadOnlySpan<byte> wireBytes)
    {
        var settings = protocolSettings;
        var decoder = new MessageDecoder(new SilenceMessageFramer(), settings.CreateMessageCheck());
        DecodedMessage? decoded = null;
        decoder.Consume(wireBytes, m => decoded = m); decoder.SilenceElapsed(m => decoded = m);
        return ReceivedDataAdapter.Create(decoded ?? throw new InvalidOperationException("Silence decoder produced no message."), settings);
    }

    private void RxDetected(object sender, SerialDataReceivedEventArgs e)
    {
        if (!ReferenceEquals(sender, port)) return;
        Telemetry.RecordReceiveNotification();
        try { receivePipeline?.SignalDataAvailable(); } catch (ObjectDisposedException) when (!active) { }
    }
    private void PinChanged(object sender, SerialPinChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, port)) return;
        ReportTrace($"pin changed: {e.EventType}", TraceFlags.Detail | TraceFlags.Noisy);
        PublishSignals(ring: e.EventType == SerialPinChange.Ring);
    }
    private void PublishSignals(bool force = false, bool ring = false)
    {
        var p = port; if (p?.IsOpen != true) return;
        try
        {
            var state = new SerialSignalState(p.RtsEnable, p.CtsHolding, p.DtrEnable, p.DsrHolding, p.CDHolding, ring);
            if (!force && lastSignalState == state) return;
            lastSignalState = state;
            ReportTrace($"signals: {state}", TraceFlags.Detail | TraceFlags.Noisy);
            var handlers = SignalsChanged; if (handlers is null) return;
            foreach (Action<SerialSignalState> handler in handlers.GetInvocationList())
                try { handler(state); } catch (Exception e) { ReportTrace($"SignalsChanged subscriber exception: {e}", TraceFlags.Detail); }
        }
        catch (Exception e) { ReportError($"signal state exception: {e.Message}"); }
    }
    private void DispatchData(ReceivedData data)
    {
        var handlers = DataReceived; if (handlers is null) return;
        foreach (Action<ReceivedData> handler in handlers.GetInvocationList())
            try { handler(data); } catch (Exception e) { ReportTrace($"DataReceived subscriber exception: {e}", TraceFlags.Detail); }
    }
    private void Dispatch(EventHandler? handlers, string name)
    {
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); } catch (Exception e) { ReportTrace($"{name} subscriber exception: {e}", TraceFlags.Detail); }
    }
    private void ReportTrace(string message, TraceFlags flags = TraceFlags.None)
    {
        var handlers = Trace; if (handlers is null) return;
        var entry = $"SerialChannel @{PortSettings.PortName} {message}";
        foreach (Action<string, TraceFlags> handler in handlers.GetInvocationList()) try { handler(entry, flags); } catch { }
    }
    private void ReportError(string message)
    {
        var handlers = Error; if (handlers is null) return;
        var entry = $"SerialChannel @{PortSettings.PortName} ERROR: {message}";
        foreach (Action<string> handler in handlers.GetInvocationList()) try { handler(entry); } catch { }
    }

    private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(SerialChannel)); }
    public void Close() => Disconnect();
    public void Dispose()
    {
        Thread? maintenance;
        lock (lifecycleLock)
        {
            if (disposed) return;
            ConnectionRequested = false;
            AbandonAcquisition();
            maintenance = StopConnectionMaintenance();
        }
        JoinWorker(maintenance);

        lock (lifecycleLock)
        {
            if (disposed) return;
            if (ConnectionState == SerialConnectionState.Connected) Dispatch(Disconnecting, nameof(Disconnecting));
            ConnectionState = SerialConnectionState.Disconnecting;
            StopWorkersAndReleaseTransport(); lastSignalState = null; disposed = true;
            ConnectionState = SerialConnectionState.Disposed; recoverySignal.Dispose();
        }
    }
}

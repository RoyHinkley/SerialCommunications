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

    public double MillisecondsSinceLastTx => txSw.Elapsed.TotalMilliseconds;
    public double MillisecondsSinceLastRx => rxSw.Elapsed.TotalMilliseconds;
    public bool ConnectionRequested { get; private set; }
    public SerialConnectionState ConnectionState { get; private set; } = SerialConnectionState.Disconnected;
    public bool RecoveryActive => recoveryActive && ConnectionRequested && ConnectionState == SerialConnectionState.Unavailable;
    public int RecoveryAttempts { get; private set; }
    public DateTimeOffset? RecoveryStartedAt { get; private set; }
    public bool Ready => ConnectionState == SerialConnectionState.Connected && port?.IsOpen == true;
    public bool HaveWork => transmitting || !commandQ.IsEmpty;
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
    private IMessageCheck txCheck = NoMessageCheck.Instance;
    private ConcurrentQueue<byte[]> commandQ = new();
    private volatile bool transmitting;
    private Thread? txThread;
    private Thread? recoveryThread;
    private volatile bool recoveryActive;
    private readonly AutoResetEvent txSignal = new(false);
    private readonly AutoResetEvent recoverySignal = new(false);
    private readonly Stopwatch txSw = new(), rxSw = new(), recoverySw = new();
    private SerialSignalState? lastSignalState;
    private SerialProtocolSettings protocolSettings = SerialProtocolSettings.None();
    private SerialProtocolSettings? pendingProtocolSettings;
    private string? recoveryFailureSignature;
    private int repeatedRecoveryFailures;

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
        var bytes = command.ToArray();
        ReportTrace($"received Command \"{BinaryDataFormatter.ToEscapedText(bytes)}\"", TraceFlags.Detail);
        commandQ.Enqueue(bytes);
        txSignal.Set();
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
        lock (lifecycleLock)
        {
            ThrowIfDisposed();
            ConnectionRequested = true;
            if (ConnectionState == SerialConnectionState.Connected) return true;
            var connected = TryAcquireTransport(false);
            StartConnectionMaintenance();
            return connected;
        }
    }

    private bool TryAcquireTransport(bool recovery)
    {
        ConnectionState = SerialConnectionState.Connecting;
        if (!recovery || repeatedRecoveryFailures < 3)
            ReportTrace(recovery ? "recovery connection attempt..." : "connecting...", recovery ? TraceFlags.Detail : TraceFlags.None);
        try
        {
            port = transportFactory.Create(PortSettings, RtsMode);
            port.DataReceived += RxDetected;
            port.PinChanged += PinChanged;
            port.Open();
            port.DiscardOutBuffer();
            port.DiscardInBuffer();
            pendingProtocolSettings = null;
            CreateCommsSession();
            active = true;
            txThread = StartThread(Transmit, "transmit");
            ConnectionState = SerialConnectionState.Connected;
            if (recovery)
            {
                Telemetry.RecordSuccessfulRecovery();
                ReportTrace($"connection recovered after {RecoveryAttempts} attempt{(RecoveryAttempts == 1 ? "" : "s")} and {recoverySw.Elapsed.TotalSeconds:0.0} s.");
                recoveryFailureSignature = null;
                repeatedRecoveryFailures = 0;
            }
            else ReportTrace("connected.");
            PublishSignals(true);
            Dispatch(Connected, nameof(Connected));
            return true;
        }
        catch (Exception e)
        {
            if (recovery) RecordRecoveryFailure(e); else ReportError($"Connect failed: {e}");
            StopWorkersAndReleaseTransport();
            ConnectionState = SerialConnectionState.Unavailable;
            if (!recovery) BeginRecoveryPeriod();
            return false;
        }
    }

    public bool Disconnect()
    {
        lock (lifecycleLock)
        {
            if (disposed) return true;
            ConnectionRequested = false;
            StopConnectionMaintenance();
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
    private void StopConnectionMaintenance()
    {
        recoveryActive = false;
        recoverySignal.Set();
        JoinWorker(recoveryThread);
        recoveryThread = null;
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
                if (!Monitor.TryEnter(lifecycleLock)) continue;
                try
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
                    TryAcquireTransport(true);
                }
                finally { Monitor.Exit(lifecycleLock); }
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
        commandQ = new();
        transmitting = false;
        txCheck = protocolSettings.CreateMessageCheck();
        lastSignalState = null;
        txSw.Reset(); rxSw.Reset(); Drain(txSignal);
        while (receivedSettings.TryDequeue(out _)) { }
        receivePipeline = new SerialMessagePipeline(
            port!, protocolSettings.CreateMessageDecoder(), HandleDecodedMessage,
            TimeSpan.FromMilliseconds(Math.Max(1, MaximumMillisecondsSilenceInMessage)),
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
        txSignal.Set();

        var pipeline = receivePipeline;
        receivePipeline = null;
        pipeline?.Dispose();

        // Detach the old transport from the channel before interrupting it. A replacement cannot be installed
        // until this lifecycle operation completes, and a worker released from a blocking call cannot accidentally
        // observe a future session's transport through the shared field.
        var p = port;
        port = null;
        var success = true;
        if (p is not null)
        {
            try { p.DataReceived -= RxDetected; } catch (Exception e) { success = false; ReportError($"DataReceived unsubscribe exception: {e}"); }
            try { p.PinChanged -= PinChanged; } catch (Exception e) { success = false; ReportError($"PinChanged unsubscribe exception: {e}"); }

            // Close and dispose before joining the transmit worker. Either operation may be the one that interrupts
            // a blocked synchronous serial write on a particular platform/driver; teardown must not depend on Close
            // alone returning that worker to managed code.
            try { p.Close(); } catch (Exception e) { success = false; ReportError($"port close exception: {e}"); }
            try { p.Dispose(); } catch (Exception e) { success = false; ReportError($"port dispose exception: {e}"); }
        }

        JoinWorker(txThread);
        txThread = null;
        transmitting = false;
        while (receivedSettings.TryDequeue(out _)) { }
        return success;
    }

    private static void JoinWorker(Thread? thread) { if (thread is not null && thread != Thread.CurrentThread && thread.IsAlive) thread.Join(); }
    private bool WorkersAlive => (txThread?.IsAlive ?? false) || receivePipeline is not null;

    private void Transmit()
    {
        ReportTrace("starting Transmit thread.", TraceFlags.Detail);
        try
        {
            byte[] tx = []; var offset = 0; txSw.Restart();
            while (active)
            {
                if (offset < tx.Length)
                {
                    try
                    {
                        if (MillisecondsBetweenBytes < 0)
                        {
                            if (MillisecondsSinceLastTx >= MillisecondsBetweenMessages)
                            {
                                port!.Write(tx, 0, tx.Length); txSw.Restart(); offset = tx.Length;
                                Telemetry.RecordMessageTransmitted();
                                ReportTrace($"Transmit: {BinaryDataFormatter.ToHexBytes(tx)}");
                            }
                            else Thread.Sleep(1);
                        }
                        else if (MillisecondsSinceLastTx >= MillisecondsBetweenBytes)
                        {
                            ReportTrace($"Transmit byte ({MillisecondsSinceLastTx:0} ms since last): {tx[offset]:X2}", TraceFlags.Detail);
                            port!.Write(tx, offset++, 1); txSw.Restart();
                            if (offset == tx.Length)
                            {
                                Telemetry.RecordMessageTransmitted();
                                ReportTrace($"Transmit: {BinaryDataFormatter.ToHexBytes(tx)}");
                            }
                        }
                        else Thread.Sleep(1);
                    }
                    catch (Exception e) { ReportError($"transmit exception: {e}"); Thread.Sleep(Math.Max(20, MillisecondsBetweenMessages)); }
                }
                else
                {
                    transmitting = false;
                    if (commandQ.TryDequeue(out var command))
                    {
                        offset = 0; tx = txCheck.Append(command); transmitting = true;
                    }
                    else txSignal.WaitOne(1000);
                }
            }
        }
        catch (Exception e) { ReportError($"fatal Transmit exception: {e}"); }
        finally { ReportTrace("ending Transmit thread.", TraceFlags.Detail); }
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
        txCheck = settings.CreateMessageCheck();
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
        Telemetry.RecordReceiveNotification();
        try { receivePipeline?.SignalDataAvailable(); } catch (ObjectDisposedException) when (!active) { }
    }
    private void PinChanged(object sender, SerialPinChangedEventArgs e)
    {
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

    private static void Drain(AutoResetEvent signal) { while (signal.WaitOne(0)) { } }
    private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(SerialChannel)); }
    public void Close() => Disconnect();
    public void Dispose()
    {
        lock (lifecycleLock)
        {
            if (disposed) return;
            ConnectionRequested = false; StopConnectionMaintenance();
            if (ConnectionState == SerialConnectionState.Connected) Dispatch(Disconnecting, nameof(Disconnecting));
            ConnectionState = SerialConnectionState.Disconnecting;
            StopWorkersAndReleaseTransport(); lastSignalState = null; disposed = true;
            ConnectionState = SerialConnectionState.Disposed; txSignal.Dispose(); recoverySignal.Dispose();
        }
    }
}
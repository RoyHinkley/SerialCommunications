namespace SerialCommunications.Serial.Protocol;

/// <summary>
/// Moves receive acquisition off the transport notification path and reports receive silence independently.
/// </summary>
/// <remarks>
/// <see cref="SignalDataAvailable"/> is deliberately limited to signaling the worker. It never reads, decodes,
/// logs, or invokes application code. The acquisition callback should drain whatever bytes are currently
/// available and feed them to the decoder in blocks. Repeated notifications naturally coalesce while the worker
/// is busy. Silence is a separate observation measured from the most recent notification.
/// </remarks>
internal sealed class ReceivePump : IDisposable
{
    private readonly Action acquireAvailable;
    private readonly Action silenceElapsed;
    private readonly Action<Exception>? acquisitionError;
    private readonly Func<TimeSpan> silenceInterval;
    private readonly AutoResetEvent signal = new(false);
    private readonly Thread worker;
    private volatile bool active = true;
    private long notificationVersion;
    private bool disposed;

    public ReceivePump(
        Action acquireAvailable,
        Action silenceElapsed,
        TimeSpan silenceInterval,
        Action<Exception>? acquisitionError = null)
        : this(acquireAvailable, silenceElapsed, () => silenceInterval, acquisitionError)
    {
        if (silenceInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(silenceInterval), "Silence interval must be greater than zero.");
    }

    public ReceivePump(
        Action acquireAvailable,
        Action silenceElapsed,
        Func<TimeSpan> silenceInterval,
        Action<Exception>? acquisitionError = null)
    {
        this.acquireAvailable = acquireAvailable ?? throw new ArgumentNullException(nameof(acquireAvailable));
        this.silenceElapsed = silenceElapsed ?? throw new ArgumentNullException(nameof(silenceElapsed));
        this.silenceInterval = silenceInterval ?? throw new ArgumentNullException(nameof(silenceInterval));
        this.acquisitionError = acquisitionError;
        worker = new Thread(Run) { Name = "serial receive acquisition", IsBackground = true };
        worker.Start();
    }

    public void SignalDataAvailable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Interlocked.Increment(ref notificationVersion);
        signal.Set();
    }

    private void Run()
    {
        long acquiredVersion = 0;
        long silenceReportedVersion = 0;
        var messageInProgress = false;
        var messageSilenceInterval = TimeSpan.Zero;

        while (active)
        {
            var version = Volatile.Read(ref notificationVersion);
            if (version == acquiredVersion)
            {
                signal.WaitOne();
                continue;
            }

            if (!messageInProgress)
            {
                messageSilenceInterval = silenceInterval();
                if (messageSilenceInterval <= TimeSpan.Zero)
                {
                    ReportError(new ArgumentOutOfRangeException(nameof(silenceInterval), "Silence interval must be greater than zero."));
                    messageSilenceInterval = TimeSpan.FromMilliseconds(1);
                }
                messageInProgress = true;
            }

            acquiredVersion = version;
            try { acquireAvailable(); }
            catch (Exception e) { ReportError(e); }

            while (active)
            {
                var observed = Volatile.Read(ref notificationVersion);
                if (observed != acquiredVersion)
                    break;

                if (signal.WaitOne(messageSilenceInterval))
                    continue;

                observed = Volatile.Read(ref notificationVersion);
                if (observed != acquiredVersion)
                    break;

                if (silenceReportedVersion != acquiredVersion)
                {
                    silenceReportedVersion = acquiredVersion;
                    try { silenceElapsed(); }
                    catch (Exception e) { ReportError(e); }
                }
                messageInProgress = false;
                break;
            }
        }
    }

    private void ReportError(Exception exception)
    {
        if (acquisitionError is null) return;
        try { acquisitionError(exception); }
        catch { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        active = false;
        signal.Set();
        if (worker != Thread.CurrentThread && worker.IsAlive) worker.Join();
        signal.Dispose();
    }
}

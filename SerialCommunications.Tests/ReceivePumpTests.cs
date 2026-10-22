using SerialCommunications.Serial.Protocol;

namespace SerialCommunications.Tests;

public sealed class ReceivePumpTests
{
    [Fact]
    public void Notification_returns_without_waiting_for_acquisition()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var pump = new ReceivePump(
            () => { entered.Set(); release.Wait(); },
            () => { },
            TimeSpan.FromSeconds(1));

        var signalTask = Task.Run(pump.SignalDataAvailable);

        Assert.True(signalTask.Wait(TimeSpan.FromSeconds(1)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
        release.Set();
    }

    [Fact]
    public void Repeated_notifications_while_acquiring_are_coalesced_into_next_acquisition()
    {
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        var acquisitions = 0;
        using var pump = new ReceivePump(
            () =>
            {
                var count = Interlocked.Increment(ref acquisitions);
                if (count == 1) { firstEntered.Set(); releaseFirst.Wait(); }
                else if (count == 2) secondEntered.Set();
            },
            () => { },
            TimeSpan.FromSeconds(1));

        pump.SignalDataAvailable();
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(1)));
        for (var i = 0; i < 20; i++) pump.SignalDataAvailable();
        releaseFirst.Set();

        Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(1)));
        Thread.Sleep(50);
        Assert.Equal(2, Volatile.Read(ref acquisitions));
    }

    [Fact]
    public void Silence_is_reported_once_after_latest_notification()
    {
        using var firstAcquired = new ManualResetEventSlim();
        using var secondAcquired = new ManualResetEventSlim();
        using var silence = new ManualResetEventSlim();
        var acquisitions = 0;
        var silences = 0;
        using var pump = new ReceivePump(
            () =>
            {
                if (Interlocked.Increment(ref acquisitions) == 1) firstAcquired.Set();
                else secondAcquired.Set();
            },
            () => { Interlocked.Increment(ref silences); silence.Set(); },
            TimeSpan.FromMilliseconds(100));

        pump.SignalDataAvailable();
        Assert.True(firstAcquired.Wait(TimeSpan.FromSeconds(1)));
        Thread.Sleep(60);
        pump.SignalDataAvailable();
        Assert.True(secondAcquired.Wait(TimeSpan.FromSeconds(1)));

        Assert.False(silence.Wait(TimeSpan.FromMilliseconds(60)));
        Assert.True(silence.Wait(TimeSpan.FromMilliseconds(500)));
        Thread.Sleep(150);
        Assert.Equal(1, Volatile.Read(ref silences));
    }

    [Fact]
    public void Acquisition_exception_does_not_stop_later_notifications()
    {
        using var recovered = new ManualResetEventSlim();
        var acquisitions = 0;
        var errors = 0;
        using var pump = new ReceivePump(
            () =>
            {
                if (Interlocked.Increment(ref acquisitions) == 1) throw new InvalidOperationException("expected");
                recovered.Set();
            },
            () => { },
            TimeSpan.FromSeconds(1),
            _ => Interlocked.Increment(ref errors));

        pump.SignalDataAvailable();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref errors) == 1, TimeSpan.FromSeconds(1)));
        pump.SignalDataAvailable();

        Assert.True(recovered.Wait(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, Volatile.Read(ref errors));
    }

    [Fact]
    public void Notification_after_disposal_is_rejected()
    {
        var pump = new ReceivePump(() => { }, () => { }, TimeSpan.FromSeconds(1));
        pump.Dispose();

        Assert.Throws<ObjectDisposedException>(pump.SignalDataAvailable);
    }
}

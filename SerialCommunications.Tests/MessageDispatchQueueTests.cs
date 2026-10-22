using SerialCommunications.Serial.Protocol;

namespace SerialBench.Tests;

public sealed class MessageDispatchQueueTests
{
    [Fact]
    public void SlowConsumerDoesNotBlockDecoding()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var secondDelivered = new ManualResetEventSlim();
        var delivered = 0;
        using var dispatch = new MessageDispatchQueue(
            new MessageDecoder(new TerminatorMessageFramer(0x03), NoMessageCheck.Instance),
            _ =>
            {
                var n = Interlocked.Increment(ref delivered);
                if (n == 1)
                {
                    entered.Set();
                    release.Wait();
                }
                else secondDelivered.Set();
            });

        dispatch.Consume(new byte[] { 0x11, 0x03 });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        var completed = Task.Run(() => dispatch.Consume(new byte[] { 0x22, 0x03 }))
            .Wait(TimeSpan.FromSeconds(1));

        Assert.True(completed);
        Assert.Equal(1, Volatile.Read(ref delivered));
        release.Set();
        Assert.True(secondDelivered.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, Volatile.Read(ref delivered));
    }

    [Fact]
    public void ConsumerExceptionsDoNotStopFollowingMessages()
    {
        using var delivered = new ManualResetEventSlim();
        using var errorSeen = new ManualResetEventSlim();
        var calls = 0;
        using var dispatch = new MessageDispatchQueue(
            new MessageDecoder(new TerminatorMessageFramer(0x03), NoMessageCheck.Instance),
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("test");
                delivered.Set();
            },
            e => { Assert.IsType<InvalidOperationException>(e); errorSeen.Set(); });

        dispatch.Consume(new byte[] { 0x11, 0x03, 0x22, 0x03 });

        Assert.True(errorSeen.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(delivered.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public void ErrorReporterExceptionsDoNotStopFollowingMessages()
    {
        using var delivered = new ManualResetEventSlim();
        var calls = 0;
        using var dispatch = new MessageDispatchQueue(
            new MessageDecoder(new TerminatorMessageFramer(0x03), NoMessageCheck.Instance),
            _ =>
            {
                if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("consumer");
                delivered.Set();
            },
            _ => throw new InvalidOperationException("reporter"));

        dispatch.Consume(new byte[] { 0x11, 0x03, 0x22, 0x03 });

        Assert.True(delivered.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public void DisposeDoesNotWaitForBlockedConsumerAndDropsQueuedMessages()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var delivered = 0;
        var dispatch = new MessageDispatchQueue(
            new MessageDecoder(new TerminatorMessageFramer(0x03), NoMessageCheck.Instance),
            _ =>
            {
                Interlocked.Increment(ref delivered);
                entered.Set();
                release.Wait();
            });

        dispatch.Consume(new byte[] { 0x11, 0x03 });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        dispatch.Consume(new byte[] { 0x22, 0x03 });

        var disposed = Task.Run(dispatch.Dispose).Wait(TimeSpan.FromSeconds(1));

        Assert.True(disposed);
        release.Set();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref delivered) == 1, TimeSpan.FromSeconds(1)));
        Assert.Equal(1, Volatile.Read(ref delivered));
    }

    [Fact]
    public void CallsAfterDisposeAreRejected()
    {
        var dispatch = new MessageDispatchQueue(
            new MessageDecoder(new TerminatorMessageFramer(0x03), NoMessageCheck.Instance),
            _ => { });
        dispatch.Dispose();

        Assert.Throws<ObjectDisposedException>(() => dispatch.Consume(new byte[] { 0x11 }));
        Assert.Throws<ObjectDisposedException>(() => dispatch.SilenceElapsed());
        Assert.Throws<ObjectDisposedException>(() => dispatch.Reset());
    }
}

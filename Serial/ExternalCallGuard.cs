namespace SerialCommunications.Serial;

/// <summary>
/// Bounds the caller's wait for a synchronous external operation. A timeout does not
/// cancel the operation; its background thread may remain blocked indefinitely.
/// </summary>
internal static class ExternalCallGuard
{
    internal enum Outcome { Completed, Faulted, TimedOut }

    internal readonly record struct Result(Outcome Status, Exception? Exception = null);

    internal static Result Execute(Action operation, TimeSpan timeout, string? threadName = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var completion = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            Exception? failure = null;
            try { operation(); }
            catch (Exception ex) { failure = ex; }
            finally { completion.TrySetResult(failure); }
        })
        {
            IsBackground = true,
            Name = threadName ?? "serial_external_call"
        };

        worker.Start();
        if (!completion.Task.Wait(timeout))
            return new Result(Outcome.TimedOut);

        var error = completion.Task.Result;
        return error is null
            ? new Result(Outcome.Completed)
            : new Result(Outcome.Faulted, error);
    }
}

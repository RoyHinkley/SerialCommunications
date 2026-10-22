namespace SerialCommunications.Serial;

/// <summary>Controls automatic attempts to restore a requested serial connection after transport loss.</summary>
/// <param name="RetryInterval">Delay between recovery attempts; must be greater than zero.</param>
/// <param name="MaximumAttempts">Maximum attempts before giving up; -1 means unlimited.</param>
/// <param name="MaximumDuration">Maximum recovery duration; <see langword="null"/> means unlimited.</param>
public sealed record SerialRecoveryPolicy(
    TimeSpan RetryInterval,
    int MaximumAttempts = -1,
    TimeSpan? MaximumDuration = null)
{
    /// <summary>Default policy: retry once per second without an attempt or duration limit.</summary>
    public static SerialRecoveryPolicy Default { get; } = new(TimeSpan.FromSeconds(1));

    /// <summary>Determines whether another recovery attempt is permitted by this policy.</summary>
    /// <param name="attemptsAlreadyMade">Number of attempts already made in the current recovery period.</param>
    /// <param name="elapsed">Elapsed time since the current recovery period began.</param>
    /// <returns><see langword="true"/> when both the attempt and duration limits permit another attempt.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is negative where only nonnegative values are meaningful.</exception>
    /// <exception cref="InvalidOperationException">The policy contains an invalid retry interval, attempt limit, or duration.</exception>
    public bool AllowsAttempt(int attemptsAlreadyMade, TimeSpan elapsed)
    {
        if (attemptsAlreadyMade < 0) throw new ArgumentOutOfRangeException(nameof(attemptsAlreadyMade));
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (RetryInterval <= TimeSpan.Zero) throw new InvalidOperationException("RetryInterval must be greater than zero.");
        if (MaximumAttempts < -1) throw new InvalidOperationException("MaximumAttempts must be -1 or greater.");
        if (MaximumDuration is { } duration && duration < TimeSpan.Zero) throw new InvalidOperationException("MaximumDuration cannot be negative.");

        return (MaximumAttempts < 0 || attemptsAlreadyMade < MaximumAttempts)
            && (MaximumDuration is null || elapsed < MaximumDuration.Value);
    }
}

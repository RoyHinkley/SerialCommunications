namespace SerialCommunications.Serial;

/// <summary>Advisory characteristics attached to a human-readable serial diagnostic trace.</summary>
/// <remarks>
/// Flags describe presentation characteristics rather than severity. Consumers decide independently whether
/// to include detailed and noisy entries; an entry may carry both flags.
/// </remarks>
[Flags]
public enum TraceFlags
{
    /// <summary>A normal operational trace that is useful without diagnostic expansion.</summary>
    None = 0,

    /// <summary>Implementation or transport detail primarily useful while troubleshooting.</summary>
    Detail = 1,

    /// <summary>A legitimate trace that may occur frequently enough for consumers to suppress separately.</summary>
    Noisy = 2
}

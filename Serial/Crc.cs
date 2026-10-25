namespace SerialCommunications.Serial;

/// <summary>Configures a 16-bit CRC calculation and check-code byte order.</summary>
public sealed class CrcOptions
{
    /// <summary>Initial 16-bit remainder loaded before processing a codeword.</summary>
    public ushort InitialValue { get; set; } = 0xFFFF;

    /// <summary>Polynomial applied by the configured bit-order calculation.</summary>
    public ushort Polynomial { get; set; } = 0xDAAE;

    /// <summary>Remainder expected after processing a valid complete codeword.</summary>
    public ushort ExpectedResidue { get; set; } = 0x82C0;

    /// <summary>Whether to one's-complement the calculated remainder before appending its two CRC bytes.</summary>
    public bool PostInvert { get; set; } = true;

    /// <summary>Whether each input byte is processed most-significant bit first; otherwise bits are processed least-significant first.</summary>
    public bool MsBitFirst { get; set; }

    /// <summary>Whether the high CRC byte is written before the low CRC byte; otherwise the low byte is written first.</summary>
    public bool MsByteFirst { get; set; }

    /// <summary>Creates the default CRC configuration.</summary>
    public CrcOptions() { }

    /// <summary>Creates a CRC configuration with all calculation and check-code options specified explicitly.</summary>
    public CrcOptions(
        ushort initialValue,
        ushort polynomial,
        ushort expectedResidue,
        bool postInvert,
        bool msBitFirst,
        bool msByteFirst)
    {
        InitialValue = initialValue;
        Polynomial = polynomial;
        ExpectedResidue = expectedResidue;
        PostInvert = postInvert;
        MsBitFirst = msBitFirst;
        MsByteFirst = msByteFirst;
    }
}

/// <summary>Incremental 16-bit CRC calculation over exact bytes.</summary>
/// <remarks>
/// The calculation is stateful: each <see cref="Update(byte)"/> advances the current remainder. Call
/// <see cref="Init"/> to restart from <see cref="CrcOptions.InitialValue"/>. Text encoding and message framing are
/// deliberately outside this type; callers must supply the exact bytes covered by the check.
/// </remarks>
public sealed class Crc
{
    private CrcOptions options;
    private ushort remainder;

    /// <summary>Gets or replaces the active options; assigning new options also reinitializes the remainder.</summary>
    public CrcOptions Options
    {
        get => options;
        set
        {
            options = value ?? throw new ArgumentNullException(nameof(value));
            Init();
        }
    }

    /// <summary>Current 16-bit remainder after the bytes processed since the last initialization.</summary>
    public ushort Code => remainder;

    /// <summary>Creates a CRC calculator using <see cref="CrcOptions"/> defaults.</summary>
    public Crc() : this(new CrcOptions()) { }

    /// <summary>Creates a CRC calculator using the supplied options.</summary>
    public Crc(CrcOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        Init();
    }

    /// <summary>Restarts the calculation at <see cref="CrcOptions.InitialValue"/>.</summary>
    public void Init() => remainder = Options.InitialValue;

    /// <summary>Returns whether the current remainder equals <see cref="CrcOptions.ExpectedResidue"/>.</summary>
    public bool Good() => remainder == Options.ExpectedResidue;

    /// <summary>Updates the remainder with one byte and returns the resulting remainder.</summary>
    public ushort Update(byte value)
    {
        if (Options.MsBitFirst)
        {
            remainder ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                var msb = remainder & 0x8000;
                remainder <<= 1;
                if (msb != 0) remainder ^= Options.Polynomial;
            }
        }
        else
        {
            remainder ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                var lsb = remainder & 1;
                remainder >>= 1;
                if (lsb != 0) remainder ^= Options.Polynomial;
            }
        }
        return remainder;
    }

    /// <summary>Updates the remainder with the first <paramref name="count"/> bytes of an array.</summary>
    public ushort Update(byte[] values, int count)
    {
        ArgumentNullException.ThrowIfNull(values);
        if ((uint)count > (uint)values.Length) throw new ArgumentOutOfRangeException(nameof(count));
        for (var i = 0; i < count; i++) Update(values[i]);
        return remainder;
    }

    /// <summary>Updates the remainder with every byte in an array.</summary>
    public ushort Update(byte[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Update(values, values.Length);
    }

    /// <summary>Updates the remainder with every byte in a span.</summary>
    public ushort Update(ReadOnlySpan<byte> values)
    {
        foreach (var value in values) Update(value);
        return remainder;
    }

    /// <summary>Constructs the checked codeword P+C by appending the configured two-byte CRC to exact payload bytes.</summary>
    /// <remarks>This operation reinitializes the calculator before processing <paramref name="payload"/>.</remarks>
    public byte[] Append(ReadOnlySpan<byte> payload)
    {
        Init();
        Update(payload);
        var checkCode = Options.PostInvert ? (ushort)~remainder : remainder;
        var result = new byte[payload.Length + 2];
        payload.CopyTo(result);
        var i = payload.Length;
        var crcBytes = BitConverter.GetBytes(checkCode);
        if (Options.MsByteFirst)
        {
            result[i++] = crcBytes[1];
            result[i] = crcBytes[0];
        }
        else
        {
            result[i++] = crcBytes[0];
            result[i] = crcBytes[1];
        }
        return result;
    }

    /// <summary>Constructs the checked codeword P+C from exact payload bytes.</summary>
    public byte[] Append(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Append(payload.AsSpan());
    }
}

namespace SerialBench.Tests;

/// <summary>A deterministic byte-level instrument model for protocol tests.</summary>
public sealed class VirtualInstrument
{
    private readonly List<Exchange> exchanges = [];

    /// <summary>Defines the response returned when an exact command is received.</summary>
    public VirtualInstrument RespondTo(ReadOnlySpan<byte> command, ReadOnlySpan<byte> response)
    {
        exchanges.Add(new Exchange(command.ToArray(), response.ToArray()));
        return this;
    }

    /// <summary>Returns a fresh response buffer for an exact command.</summary>
    public byte[] Respond(ReadOnlySpan<byte> command)
    {
        foreach (var exchange in exchanges)
            if (command.SequenceEqual(exchange.Command))
                return exchange.Response.ToArray();

        throw new InvalidOperationException($"No virtual-instrument response is defined for command {Convert.ToHexString(command)}.");
    }

    /// <summary>Splits a response at the requested byte offsets without changing its contents.</summary>
    public static IReadOnlyList<byte[]> Fragment(ReadOnlySpan<byte> response, params int[] offsets)
    {
        var result = new List<byte[]>(offsets.Length + 1);
        var previous = 0;

        foreach (var offset in offsets)
        {
            if (offset <= previous || offset >= response.Length)
                throw new ArgumentOutOfRangeException(nameof(offsets), "Fragment offsets must be strictly increasing and inside the response.");

            result.Add(response[previous..offset].ToArray());
            previous = offset;
        }

        result.Add(response[previous..].ToArray());
        return result;
    }

    private sealed record Exchange(byte[] Command, byte[] Response);
}

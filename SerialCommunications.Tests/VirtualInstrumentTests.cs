namespace SerialBench.Tests;

public sealed class VirtualInstrumentTests
{
    [Fact]
    public void Exact_command_returns_defined_binary_response()
    {
        var instrument = new VirtualInstrument()
            .RespondTo([0x02, 0x03, 0x00, 0x01], [0x02, 0x03, 0x10, 0x03, 0xFF]);

        var response = instrument.Respond([0x02, 0x03, 0x00, 0x01]);

        Assert.Equal(new byte[] { 0x02, 0x03, 0x10, 0x03, 0xFF }, response);
    }

    [Fact]
    public void Response_buffers_are_not_shared_between_exchanges()
    {
        var instrument = new VirtualInstrument().RespondTo([0x01], [0x10, 0x20]);

        var first = instrument.Respond([0x01]);
        first[0] = 0xFF;
        var second = instrument.Respond([0x01]);

        Assert.Equal(new byte[] { 0x10, 0x20 }, second);
    }

    [Fact]
    public void Fragmentation_preserves_exact_wire_bytes()
    {
        var response = new byte[] { 0x02, 0x03, 0x10, 0x03, 0x20, 0x95, 0xF8 };

        var fragments = VirtualInstrument.Fragment(response, 1, 3, 6);
        var reconstructed = fragments.SelectMany(fragment => fragment).ToArray();

        Assert.Equal(response, reconstructed);
        Assert.Equal(new[] { 1, 2, 3, 1 }, fragments.Select(fragment => fragment.Length));
    }

    [Fact]
    public void Undefined_command_fails_loudly()
    {
        var instrument = new VirtualInstrument().RespondTo([0x01], [0x02]);

        var error = Assert.Throws<InvalidOperationException>(() => instrument.Respond([0x03]));

        Assert.Contains("03", error.Message);
    }
}

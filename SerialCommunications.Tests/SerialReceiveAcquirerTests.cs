using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class SerialReceiveAcquirerTests
{
    [Fact]
    public void Drain_reads_only_bytes_reported_available()
    {
        var transport = new FakeSerialTransport();
        transport.EnqueueReceived(0x10, 0x20, 0x30);
        var chunks = new List<byte[]>();
        var acquirer = new SerialReceiveAcquirer(transport, bytes => chunks.Add(bytes.ToArray()));

        var total = acquirer.DrainAvailable();

        Assert.Equal(3, total);
        Assert.Equal(0, transport.BytesToRead);
        Assert.Equal(1, transport.ReadCalls);
        Assert.Single(chunks);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, chunks[0]);
    }

    [Fact]
    public void Drain_uses_bounded_blocks_until_transport_is_empty()
    {
        var transport = new FakeSerialTransport();
        transport.EnqueueReceived(Enumerable.Range(0, 10).Select(i => (byte)i).ToArray());
        var chunks = new List<byte[]>();
        var acquirer = new SerialReceiveAcquirer(transport, bytes => chunks.Add(bytes.ToArray()), bufferSize: 4);

        var total = acquirer.DrainAvailable();

        Assert.Equal(10, total);
        Assert.Equal(3, transport.ReadCalls);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, chunks[0]);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, chunks[1]);
        Assert.Equal(new byte[] { 8, 9 }, chunks[2]);
    }

    [Fact]
    public void Empty_transport_does_not_issue_a_blocking_read()
    {
        var transport = new FakeSerialTransport();
        var acquirer = new SerialReceiveAcquirer(transport, _ => throw new InvalidOperationException());

        var total = acquirer.DrainAvailable();

        Assert.Equal(0, total);
        Assert.Equal(0, transport.ReadCalls);
    }

    [Fact]
    public void Consumer_runs_synchronously_before_reusable_buffer_is_overwritten()
    {
        var transport = new FakeSerialTransport();
        transport.EnqueueReceived(1, 2, 3, 4, 5, 6);
        var chunks = new List<byte[]>();
        var acquirer = new SerialReceiveAcquirer(transport, bytes => chunks.Add(bytes.ToArray()), bufferSize: 3);

        acquirer.DrainAvailable();

        Assert.Equal(new byte[] { 1, 2, 3 }, chunks[0]);
        Assert.Equal(new byte[] { 4, 5, 6 }, chunks[1]);
    }
}

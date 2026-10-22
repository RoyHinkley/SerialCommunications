using SerialCommunications.Serial;
using SerialCommunications.Serial.Protocol;

namespace SerialBench.Tests;

public sealed class SerialMessagePipelineTests
{
    [Fact]
    public void Transport_notification_flows_through_acquisition_decoding_and_dispatch()
    {
        var transport = new FakeSerialTransport();
        using var delivered = new ManualResetEventSlim();
        DecodedMessage? received = null;
        using var pipeline = new SerialMessagePipeline(
            transport,
            new MessageDecoder(new TerminatorMessageFramer(0x03), NoMessageCheck.Instance),
            message => { received = message; delivered.Set(); },
            TimeSpan.FromMilliseconds(100));

        transport.EnqueueReceived(0x10, 0x20, 0x03);
        pipeline.SignalDataAvailable();

        Assert.True(delivered.Wait(TimeSpan.FromSeconds(2)));
        Assert.NotNull(received);
        Assert.Equal(new byte[] { 0x10, 0x20 }, received!.Payload.ToArray());
        Assert.Equal(0, transport.BytesToRead);
    }

    [Fact]
    public void Silence_is_reported_to_silence_framer_after_acquisition()
    {
        var transport = new FakeSerialTransport();
        using var delivered = new ManualResetEventSlim();
        DecodedMessage? received = null;
        using var pipeline = new SerialMessagePipeline(
            transport,
            new MessageDecoder(new SilenceMessageFramer(), NoMessageCheck.Instance),
            message => { received = message; delivered.Set(); },
            TimeSpan.FromMilliseconds(25));

        transport.EnqueueReceived(0x41, 0x42, 0x43);
        pipeline.SignalDataAvailable();

        Assert.True(delivered.Wait(TimeSpan.FromSeconds(2)));
        Assert.NotNull(received);
        Assert.Equal(new byte[] { 0x41, 0x42, 0x43 }, received!.Payload.ToArray());
    }

    [Fact]
    public void Legacy_Aeon_settings_run_end_to_end_through_composed_pipeline()
    {
        var transport = new FakeSerialTransport();
        var options = new CrcOptions
        {
            Polynomial = 0xA001,
            InitialValue = 0xFFFF,
            ExpectedResidue = 0,
            PostInvert = false,
            MsBitFirst = false,
            MsByteFirst = false,
            TermChar = 0x03,
            OmitTermChar = false
        };
        var settings = SerialProtocolSettings.From(options, suppressInvalidMessages: false);
        var payload = new byte[] { 0x41, 0x0D, 0x0A, 0x42 };
        var wire = new Crc(options).Append(payload);
        using var delivered = new ManualResetEventSlim();
        DecodedMessage? received = null;
        using var pipeline = new SerialMessagePipeline(
            transport,
            settings.CreateMessageDecoder(),
            message => { received = message; delivered.Set(); },
            TimeSpan.FromMilliseconds(25));

        transport.EnqueueReceived(wire);
        pipeline.SignalDataAvailable();

        Assert.True(delivered.Wait(TimeSpan.FromSeconds(2)));
        Assert.NotNull(received);
        Assert.True(received!.CheckValid);
        Assert.Equal(payload, received.Payload.ToArray());
        Assert.Equal(wire[..^1], received.Frame.ToArray());
    }
}

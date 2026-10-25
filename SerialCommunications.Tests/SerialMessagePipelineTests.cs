using SerialCommunications.Serial;
using SerialCommunications.Serial.Protocol;

namespace SerialCommunications.Tests;

public sealed class SerialMessagePipelineTests
{
    [Fact]
    public void Transport_notification_flows_through_acquisition_decoding_and_dispatch()
    {
        var transport = new FakeSerialTransport();
        var settings = SerialProtocolSettings.None(FrameLayout.PE, end: [0x03]);
        using var delivered = new ManualResetEventSlim();
        DecodedMessage? received = null;
        using var pipeline = new SerialMessagePipeline(
            transport,
            settings.CreateMessageDecoder(),
            message => { received = message; delivered.Set(); },
            TimeSpan.FromMilliseconds(100));

        byte[] wire = [0x10, 0x20, 0x03];
        transport.EnqueueReceived(wire);
        pipeline.SignalDataAvailable();

        Assert.True(delivered.Wait(TimeSpan.FromSeconds(2)));
        Assert.NotNull(received);
        Assert.Equal(wire, received!.Frame.ToArray());
        Assert.Equal(new byte[] { 0x10, 0x20 }, received.Payload.ToArray());
        Assert.Equal(0, transport.BytesToRead);
    }

    [Fact]
    public void Silence_is_reported_to_layout_aware_decoder_after_acquisition()
    {
        var transport = new FakeSerialTransport();
        var settings = SerialProtocolSettings.None(FrameLayout.P);
        using var delivered = new ManualResetEventSlim();
        DecodedMessage? received = null;
        using var pipeline = new SerialMessagePipeline(
            transport,
            settings.CreateMessageDecoder(),
            message => { received = message; delivered.Set(); },
            TimeSpan.FromMilliseconds(25));

        byte[] wire = [0x41, 0x42, 0x43];
        transport.EnqueueReceived(wire);
        pipeline.SignalDataAvailable();

        Assert.True(delivered.Wait(TimeSpan.FromSeconds(2)));
        Assert.NotNull(received);
        Assert.Equal(wire, received!.Frame.ToArray());
        Assert.Equal(wire, received.Payload.ToArray());
    }

    [Fact]
    public void Aeon_PCE_settings_run_end_to_end_through_composed_pipeline()
    {
        var transport = new FakeSerialTransport();
        var options = new CrcOptions
        {
            Polynomial = 0xA001,
            InitialValue = 0xFFFF,
            ExpectedResidue = 0,
            PostInvert = false,
            MsBitFirst = false,
            MsByteFirst = false
        };
        var settings = SerialProtocolSettings.Crc16(options, FrameLayout.PCE, end: [0x03]);
        var payload = new byte[] { 0x41, 0x0D, 0x0A, 0x42 };
        var wire = settings.EncodeFrame(payload);
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
        Assert.Equal(wire, received.Frame.ToArray());
        Assert.Equal(payload, received.Payload.ToArray());
    }
}

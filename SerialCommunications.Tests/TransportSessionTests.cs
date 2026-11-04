using SerialCommunications.Serial;

namespace SerialCommunications.Tests;

public sealed class TransportSessionTests
{
    [Fact]
    public void Acquisition_mediation_and_shutdown_own_one_transport()
    {
        var transport = new FakeSerialTransport();
        var session = new TransportSession(transport);
        Assert.Equal(TransportSession.Phase.Acquisition, session.CurrentPhase);
        Assert.Equal(TransportSession.OperationResult.Rejected, session.GetOpen(out var open));

        Assert.Equal(TransportSession.OperationResult.Succeeded, session.Acquire());
        Assert.Equal(TransportSession.Phase.Mediation, session.CurrentPhase);
        Assert.Equal(TransportSession.OperationResult.Succeeded, session.GetOpen(out open));
        Assert.True(open);

        Assert.Equal(
            TransportSession.OperationResult.Succeeded,
            session.Shutdown(TimeSpan.FromSeconds(2), _ => { }));
        Assert.Equal(TransportSession.Phase.Shutdown, session.CurrentPhase);
        Assert.Equal(TransportSession.OperationResult.Rejected, session.GetOpen(out _));
        Assert.Equal(1, transport.CloseCalls);
        Assert.Equal(1, transport.DisposeCalls);
        Assert.Equal(TransportSession.OperationResult.Rejected,
            session.Shutdown(TimeSpan.FromSeconds(2), _ => { }));
    }

    [Fact]
    public void Quarantine_is_irreversible_and_suppresses_transport_mediation()
    {
        var transport = new FakeSerialTransport();
        var session = new TransportSession(transport);
        Assert.Equal(TransportSession.OperationResult.Succeeded, session.Acquire());
        var notifications = 0;
        var quarantineEvents = 0;
        session.DataReceived += (_, _) => notifications++;
        session.Quarantined += (_, _) => quarantineEvents++;

        transport.RaiseDataReceived();
        Assert.Equal(1, notifications);

        session.Quarantine("test fault");
        session.Quarantine("duplicate");
        transport.RaiseDataReceived();
        Assert.Equal(TransportSession.OperationResult.Quarantined, session.WriteBytes([0x41], 0, 1));

        Assert.True(session.IsQuarantined);
        Assert.Equal(1, quarantineEvents);
        Assert.Equal(1, notifications);
        Assert.Equal(0, transport.WriteCalls);
        Assert.Equal(TransportSession.OperationResult.Quarantined, session.GetOpen(out _));
        Assert.Equal(TransportSession.OperationResult.Quarantined, session.GetBytesToRead(out var count));
        Assert.Equal(0, count);
        Assert.Equal(TransportSession.OperationResult.Quarantined, session.Acquire());

        // Quarantine does not prevent one-shot internal resource cleanup.
        Assert.Equal(TransportSession.OperationResult.Succeeded,
            session.Shutdown(TimeSpan.FromSeconds(2), _ => { }));
        Assert.Equal(1, transport.DisposeCalls);
    }

    [Fact]
    public void Typed_results_distinguish_normal_zero_data_from_rejected_or_failed_operations()
    {
        var transport = new FakeSerialTransport();
        var session = new TransportSession(transport);
        Assert.Equal(TransportSession.OperationResult.Rejected, session.ReadBytes(new byte[1], 0, 1, out var count));
        Assert.Equal(0, count);

        Assert.Equal(TransportSession.OperationResult.Succeeded, session.Acquire());
        Assert.Equal(TransportSession.OperationResult.Succeeded, session.ReadBytes(new byte[1], 0, 1, out count));
        Assert.Equal(0, count);

        transport.ReadException = new IOException("read failure");
        Assert.Equal(TransportSession.OperationResult.Failed, session.ReadBytes(new byte[1], 0, 1, out count));
        Assert.IsType<IOException>(session.LastFailure);
        Assert.Equal(0, count);

        session.Quarantine("test");
        Assert.Equal(TransportSession.OperationResult.Quarantined, session.ReadBytes(new byte[1], 0, 1, out count));
        Assert.Equal(0, count);
        session.Shutdown(TimeSpan.FromSeconds(2), _ => { });
    }

    [Fact]
    public void Failed_acquisition_keeps_session_out_of_mediation_and_can_release_transport()
    {
        var transport = new FakeSerialTransport { DiscardInException = new IOException("discard failed") };
        var session = new TransportSession(transport);
        Assert.Equal(TransportSession.OperationResult.Failed, session.Acquire());
        Assert.IsType<IOException>(session.LastFailure);
        Assert.Equal(TransportSession.Phase.Acquisition, session.CurrentPhase);
        Assert.False(session.IsOpen);
        Assert.Equal(TransportSession.OperationResult.Succeeded,
            session.Shutdown(TimeSpan.FromSeconds(2), _ => { }));
        Assert.Equal(1, transport.CloseCalls);
        Assert.Equal(1, transport.DisposeCalls);
    }
}

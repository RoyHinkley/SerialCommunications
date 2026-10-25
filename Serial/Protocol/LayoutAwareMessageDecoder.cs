namespace SerialCommunications.Serial.Protocol;

/// <summary>Incrementally scans and decodes messages according to a <see cref="FrameLayout"/>.</summary>
/// <remarks>
/// Ordinary layouts use E, when present, as the explicit frame boundary and otherwise complete on receive silence.
/// <see cref="FrameLayout.PCE"/> additionally uses the configured check to distinguish E from an identical byte
/// sequence occurring within C. Full frame bytes are preserved; only P is exposed as payload.
/// </remarks>
internal sealed class LayoutAwareMessageDecoder : IMessageDecoder
{
    private readonly SerialProtocolSettings settings;
    private readonly IMessageCheck check;
    private readonly List<byte> buffer = [];
    private int pendingEnd = -1;
    private int nextEndSearch;

    public LayoutAwareMessageDecoder(SerialProtocolSettings settings)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        check = settings.CreateMessageCheck();
    }

    public bool HasPendingData => buffer.Count != 0;

    public void Consume(ReadOnlySpan<byte> bytes, Action<DecodedMessage> messageReady)
    {
        ArgumentNullException.ThrowIfNull(messageReady);
        foreach (var value in bytes)
        {
            buffer.Add(value);
            if (HasEnd) ScanForEnd(messageReady);
        }
    }

    public void SilenceElapsed(Action<DecodedMessage> messageReady)
    {
        ArgumentNullException.ThrowIfNull(messageReady);
        if (buffer.Count == 0) return;
        if (!HasEnd) Emit(buffer.Count, messageReady);
        else if (settings.Layout == FrameLayout.PCE && pendingEnd >= 0)
            Emit(pendingEnd + settings.End.Length, messageReady);
    }

    public void Reset()
    {
        buffer.Clear();
        pendingEnd = -1;
        nextEndSearch = 0;
    }

    private bool HasEnd => settings.Layout is FrameLayout.PE or FrameLayout.PEC or FrameLayout.SPE or FrameLayout.SPEC or FrameLayout.PCE;
    private bool CheckAfterEnd => settings.Layout is FrameLayout.PEC or FrameLayout.SPEC;

    private void ScanForEnd(Action<DecodedMessage> messageReady)
    {
        if (settings.End.Length == 0) return;
        while (true)
        {
            if (CheckAfterEnd && pendingEnd >= 0)
            {
                var pendingLength = pendingEnd + settings.End.Length + settings.CheckCodeSize;
                if (buffer.Count < pendingLength) return;
                Emit(pendingLength, messageReady);
                continue;
            }

            if (settings.Layout == FrameLayout.PCE && pendingEnd >= 0 &&
                buffer.Count - (pendingEnd + settings.End.Length) > settings.CheckCodeSize)
            {
                Emit(pendingEnd + settings.End.Length, messageReady);
                continue;
            }

            var endIndex = FindSequence(buffer, settings.End, nextEndSearch);
            if (endIndex < 0)
            {
                nextEndSearch = Math.Max(0, buffer.Count - settings.End.Length + 1);
                return;
            }
            nextEndSearch = endIndex + 1;

            if (settings.Layout == FrameLayout.PCE)
            {
                if (TryEmitPceCandidate(endIndex, messageReady)) continue;
                if (pendingEnd < 0) pendingEnd = endIndex;
                continue;
            }

            var frameLength = endIndex + settings.End.Length + (CheckAfterEnd ? settings.CheckCodeSize : 0);
            if (buffer.Count < frameLength)
            {
                pendingEnd = endIndex;
                return;
            }
            Emit(frameLength, messageReady);
        }
    }

    private bool TryEmitPceCandidate(int endIndex, Action<DecodedMessage> messageReady)
    {
        if (endIndex < settings.CheckCodeSize) return false;
        var checkFrame = buffer.GetRange(0, endIndex).ToArray();
        var valid = check.TryValidate(checkFrame, out var payload);
        if (!valid || ContainsSequence(payload, settings.End)) return false;
        Emit(endIndex + settings.End.Length, messageReady);
        return true;
    }

    private void Emit(int frameLength, Action<DecodedMessage> messageReady)
    {
        var frame = buffer.GetRange(0, frameLength).ToArray();
        var (payload, valid) = Decode(frame);
        messageReady(new DecodedMessage(frame, payload, valid));
        buffer.RemoveRange(0, frameLength);
        pendingEnd = -1;
        nextEndSearch = 0;
    }

    private (byte[] Payload, bool Valid) Decode(byte[] frame)
    {
        var payloadStart = 0;
        if (settings.Layout is FrameLayout.SP or FrameLayout.SPE or FrameLayout.SPC or FrameLayout.SPEC)
        {
            var startIndex = FindSequence(frame, settings.Start, 0);
            payloadStart = startIndex < 0 ? frame.Length : startIndex + settings.Start.Length;
        }

        var endIndex = settings.Layout == FrameLayout.PCE
            ? frame.Length - settings.End.Length
            : HasEnd ? FindSequence(frame, settings.End, payloadStart) : -1;
        var payloadEnd = endIndex >= 0 ? endIndex : frame.Length;
        if (settings.Layout is FrameLayout.PC or FrameLayout.SPC)
            payloadEnd = Math.Max(payloadStart, frame.Length - settings.CheckCodeSize);
        else if (settings.Layout == FrameLayout.PCE)
            payloadEnd = Math.Max(payloadStart, endIndex - settings.CheckCodeSize);

        var payload = frame[payloadStart..payloadEnd];
        if (settings.CheckKind == MessageCheckKind.None) return (payload, true);

        byte[] checkFrame;
        if (settings.Layout is FrameLayout.PC or FrameLayout.SPC)
            checkFrame = frame[payloadStart..];
        else if (settings.Layout is FrameLayout.PEC or FrameLayout.SPEC)
        {
            if (endIndex < 0) return (payload, false);
            checkFrame = [.. payload, .. frame[(endIndex + settings.End.Length)..]];
        }
        else if (settings.Layout == FrameLayout.PCE)
        {
            if (endIndex < settings.CheckCodeSize) return (payload, false);
            checkFrame = frame[payloadStart..endIndex];
        }
        else
            checkFrame = payload;

        return (payload, check.TryValidate(checkFrame, out _));
    }

    private static int FindSequence(IReadOnlyList<byte> source, IReadOnlyList<byte> sequence, int start)
    {
        if (sequence.Count == 0) return -1;
        for (var i = Math.Max(0, start); i <= source.Count - sequence.Count; i++)
        {
            var match = true;
            for (var j = 0; j < sequence.Count; j++)
                if (source[i + j] != sequence[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    private static bool ContainsSequence(IReadOnlyList<byte> source, IReadOnlyList<byte> sequence) =>
        FindSequence(source, sequence, 0) >= 0;
}

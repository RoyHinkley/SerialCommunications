# SerialCommunications

SerialCommunications is a reusable .NET serial communications library for applications that need explicit control over serial transport lifecycle, framing, message checks, diagnostics, telemetry, pacing, and connection recovery.

It was developed as part of [SerialBench](https://github.com/RoyHinkley/SerialBench), an interactive serial communications workbench, and extracted into this independent repository at the October 2026 project split. Earlier development history remains in the SerialBench repository, which was named `SerialPortTerminal` before the split. The extracted library begins here from that tested pre-split state.

## Scope

The library is intentionally concerned with communications mechanisms rather than instrument semantics. It provides:

- serial-port configuration and transport ownership;
- explicit connection intent and connection state;
- automatic recovery when an intended connection becomes unavailable;
- message and byte pacing;
- silence- and terminator-based framing;
- configurable CRC-16, zero-seeded XOR, and zero-seeded additive checksum checks;
- optional suppression of messages that fail their configured check;
- exact byte-oriented received-message evidence;
- trace and error diagnostics;
- lifetime communications telemetry; and
- reusable binary formatting and CRC/check helpers.

Request/response association, retries, polling policy, instrument state machines, protocol-specific field meanings, operator notification, persistence, and presentation belong to consumers of the library rather than to SerialCommunications.

## Design principles

Three states are deliberately kept distinct:

1. **Connection intent** — whether the consumer currently wants a connection.
2. **Transport availability** — whether the serial transport is actually connected and usable.
3. **Communications health** — whether messages are timely and valid.

A timeout, malformed response, failed message check, or isolated I/O error is diagnostic evidence; it does not by itself mean that the transport has disappeared.

Received data is byte-oriented. `ReceivedData` retains the exact framed bytes and payload bytes together with applicable message-check evidence. Text decoding and presentation are consumer responsibilities.

## Basic use

```csharp
using SerialCommunications.Serial;

var configuration = new SerialChannelConfiguration(
    new SerialPortSettings("COM3", 9600))
{
    RtsMode = SerialChannel.RtsModes.Enabled,
    MaximumMillisecondsSilenceInMessage = 5,
    ProtocolSettings = SerialProtocolSettings.None()
};

using var channel = new SerialChannel(configuration);

channel.DataReceived += data =>
{
    Console.WriteLine(BinaryDataFormatter.ToEscapedText(data.PayloadBytes.Span));
};

channel.Error += Console.Error.WriteLine;

if (channel.Connect())
    channel.Command(new byte[] { 0x52, 0x0D });
```

`SerialChannel.ConnectionRequested`, `ConnectionState`, and `Ready` expose the corresponding lifecycle facts. Calling `Disconnect()` intentionally clears connection intent; unexpected transport loss while connection remains requested is handled by the configured recovery policy.

## Message framing and checks

`SerialProtocolSettings` defines framing and message-check behavior. The reusable checks are:

- configurable 16-bit CRC;
- byte XOR beginning with zero; and
- additive byte sum beginning with zero, retaining the low eight bits.

These are calculation mechanisms, not protocol descriptions. Protocol-specific byte selection, bypass rules, textual check representations, and field names such as BCC belong above this layer.

Live protocol-setting changes take effect immediately when no incomplete received message is buffered. If a message is already in progress, the old decoder completes that message and the new settings take effect at the next message boundary.

## Diagnostics and telemetry

`SerialChannel` exposes separate trace and error events. `TraceFlags.Detail` and `TraceFlags.Noisy` are independent classifications that allow consumers to choose their own presentation and logging policy.

`SerialChannelTelemetry` provides lifetime counters for received bytes and messages, transmitted messages, message-check failures, receive notifications, recovery attempts, and successful recoveries. `Reset()` clears telemetry measurements without altering channel operation or lifecycle state.

## Building

The project currently targets .NET 10 and depends on `System.IO.Ports`.

```text
dotnet build SerialCommunications.csproj
```

The repository also contains `SerialCommunications.slnx` for IDE/solution use.

## License and provenance

SerialCommunications is licensed under the GNU General Public License version 3; see `LICENSE.txt`.

The code was extracted from the GPL-licensed SerialBench/SerialPortTerminal codebase rather than independently reimplemented. The SerialBench repository preserves the earlier development history and the ancestry of the extracted code.
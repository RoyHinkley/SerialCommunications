# SerialCommunications

SerialCommunications is a reusable .NET library for applications that need direct, explicit control over serial communications. It handles serial-port ownership, connection lifecycle and recovery, receive framing, message checks, transmit pacing, diagnostics, telemetry, and faithful byte-oriented message delivery without imposing instrument-specific command/response semantics.

It is the communications engine used by [SerialBench](https://github.com/RoyHinkley/SerialBench), an interactive serial-device workbench, but is designed to be used independently by automated applications such as instrument-control software.

## What the library provides

- Serial-port configuration and transport ownership.
- Explicit connection intent and connection state.
- Automatic recovery when a requested connection becomes unavailable.
- Message and inter-byte transmit pacing.
- Configurable frame layouts with literal start/end sequences and silence completion when no usable end sequence is present.
- Configurable CRC-16 message checking.
- Optional suppression of received messages that fail their configured check.
- Exact byte-oriented received-message evidence.
- Separate trace and error diagnostics.
- Lifetime communications telemetry.
- Reusable binary formatting and CRC helpers.

The library intentionally stops at the communications boundary. Request/response association, response timeouts, retries, polling policy, instrument state machines, protocol-specific field meanings, operator notification, persistence, and presentation belong to the consuming application.

## Adding it to a project

SerialCommunications currently targets .NET 10 and is maintained as a source repository rather than a NuGet package. Add `SerialCommunications.csproj` as a project reference from the consuming solution. For SerialBench development the two repositories are cloned as siblings, but another consumer may arrange the source tree differently and reference the project accordingly.

The library depends on `System.IO.Ports`.

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

`Command` accepts bytes rather than text. This is deliberate: encoding and protocol representation belong to the consumer. `BinaryDataFormatter` is available when escaped text or hexadecimal byte presentation is useful.

Call `Disconnect()` when the application no longer wants the connection. `SerialChannel` is disposable; disposal performs final teardown.

## Connection lifecycle

The library keeps three concepts separate:

1. **Connection intent** — `ConnectionRequested` records whether the consumer currently wants a connection.
2. **Transport availability** — `ConnectionState` and `Ready` describe whether the serial transport is actually connected and usable.
3. **Communications health** — timeouts, malformed messages, failed checks, and similar evidence describe communication quality rather than connection intent or physical availability.

`Connect()` establishes connection intent and attempts to acquire the transport. If the requested endpoint is unavailable, the configured `SerialRecoveryPolicy` governs subsequent automatic attempts. If an established transport later disappears, SerialChannel releases it and attempts to reacquire a fresh transport while connection intent remains true.

A `Connect()` request made while the channel is already connecting or connected is rejected and reported through `Error`. `Disconnect()` deliberately clears connection intent, abandons an in-progress acquisition, and stops recovery. A consumer that intentionally wants to cycle or supersede a connection should explicitly call `Disconnect()` and then `Connect()`.

## Framing and message checks

`SerialProtocolSettings` defines the wire frame using four symbols: S is a literal start sequence, P is the application payload, E is a literal end sequence, and C is the message check field. Supported layouts are `P`, `PE`, `PC`, `PEC`, `SP`, `SPE`, `SPC`, `SPEC`, and the exceptional legacy ordering `PCE`.

S and E may contain one or more arbitrary bytes and may also be empty. A layout containing C requires a configured check method. Configured S, E, or check values that are not named by the selected layout remain dormant rather than making the configuration invalid. Message checks cover P only; S and E do not participate.

CRC-16 is the currently supported reusable check calculation. Its initial remainder, polynomial, expected residue, post-inversion, bit order, and check-byte order are configurable through `CrcOptions`. Protocol-specific check selection, bypass values, textual check representations, field meanings, and ACK/NAK interpretation belong above this layer.

Transmit and receive use the same `SerialProtocolSettings` snapshot semantics. `Command()` accepts P and the channel constructs the complete wire frame. If protocol settings change while no incomplete received message is buffered, the new settings take effect immediately. If a message is already in progress, the existing decoder completes that message and the new settings take effect exactly at its boundary, including for following bytes in the same transport chunk. Each transmitted command is framed from one complete protocol snapshot.

## Receiving data

`DataReceived` supplies a `ReceivedData` value containing byte-oriented evidence about the completed message. `WireBytes` preserves the complete recognized wire frame, including framing and check elements; `PayloadBytes` contains P. Applicable check results and CRC-specific evidence are retained separately. Text decoding is intentionally not performed by the library.

`SerialChannelTelemetry.MessagesReceived` counts framed/decoded messages even when a failed check causes a message to be suppressed from consumer delivery. `CheckFailures` independently counts failed checks.

Consumer event handling is asynchronous. The communications layer does not deliberately discard decoded messages merely because application code is slow; consumers that need bounded admission or back-pressure should impose that policy above SerialCommunications.

## Diagnostics and telemetry

`SerialChannel` exposes separate trace and error events. Errors are not duplicated as ordinary trace events.

Trace events may carry `TraceFlags.Detail`, `TraceFlags.Noisy`, both, or neither. These are independent classifications so the consuming application can choose its own display, logging, and filtering policy.

`SerialChannelTelemetry` maintains lifetime counters for received bytes and messages, transmitted messages, message-check failures, receive notifications, recovery attempts, and successful recoveries. `Telemetry.Reset()` clears those measurements without changing channel operation, connection intent, or connection state.

## Building and testing

From the repository root:

```text
dotnet build SerialCommunications.slnx
dotnet test SerialCommunications.slnx
```

The solution contains both the library and `SerialCommunications.Tests`. The ordinary test suite exercises framing, checks, receive processing, lifecycle/recovery behavior, telemetry, and related communications semantics without depending on a GUI or physical serial hardware. Real-port integration tests are opt-in so normal CI remains hardware-independent.

## Design boundary

SerialCommunications should remain useful to both interactive tools and unattended instrument software. In particular, it should report communications facts rather than decide application policy. It should not decide which response belongs to a request, how many responses an instrument should produce, when an application should retry a command, whether an operator should be notified, or how a protocol-specific state machine advances.

This boundary is intentional rather than an omission: those policies vary substantially between devices while the underlying serial mechanics are reusable.

## Known limitation

`SerialChannel.RtsModes.Toggle` represents a demonstrated transport requirement, but RTS Toggle is not yet implemented by the current .NET `SerialPort` transport. Selecting it currently causes acquisition to fail as unsupported. Enabled and Disabled RTS modes are supported subject to the selected handshake mode; when RTS/CTS handshaking owns RTS, the transport does not also assign `RtsEnable` manually.

## License and provenance

SerialCommunications is licensed under the GNU General Public License version 3; see `LICENSE.txt`.

The library was developed within SerialBench and extracted into this independent repository in October 2026. Earlier development history remains in the SerialBench repository, which was named `SerialPortTerminal` before the split.

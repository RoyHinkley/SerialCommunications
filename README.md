# SerialCommunications

SerialCommunications is a reusable .NET library for applications that need direct, explicit control over serial communications. It handles serial-port ownership, connection lifecycle and recovery, receive framing, message checks, transmit pacing, diagnostics, telemetry, and faithful byte-oriented message delivery without imposing instrument-specific command/response semantics.

It is the communications engine used by [SerialBench](https://github.com/RoyHinkley/SerialBench), an interactive serial-device workbench, but is designed to be used independently by automated applications such as instrument-control software.

## What the library provides

- Serial-port configuration and transport ownership.
- Explicit connection intent and connection state.
- Automatic recovery when a requested connection becomes unavailable.
- Message and inter-byte transmit pacing.
- Silence- and terminator-based receive framing.
- Configurable CRC-16, zero-seeded XOR, and zero-seeded additive checksum checks.
- Optional suppression of received messages that fail their configured check.
- Exact byte-oriented received-message evidence.
- Separate trace and error diagnostics.
- Lifetime communications telemetry.
- Reusable binary formatting and CRC/check helpers.

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

`Disconnect()` deliberately clears connection intent and stops recovery. A consumer that intentionally wants to cycle a connection should explicitly call `Disconnect()` and then `Connect()`.

## Framing and message checks

`SerialProtocolSettings` defines receive framing and message-check behavior. SerialCommunications supports terminator-based and silence-based message boundaries and these reusable check calculations:

- configurable 16-bit CRC;
- XOR of the applicable bytes, beginning with zero; and
- additive byte sum beginning with zero, retaining the low eight bits.

These are calculation mechanisms, not complete instrument protocols. A protocol that calls an XOR value a BCC, represents a checksum as ASCII hexadecimal, excludes particular fields, or defines a bypass value should implement those protocol-specific rules above this layer.

Protocol settings can be changed while a channel is active. If no incomplete received message is buffered, the new settings take effect immediately. If a message is already in progress, the existing decoder completes that message and the new settings take effect exactly at the next message boundary.

## Receiving data

`DataReceived` supplies a `ReceivedData` value containing byte-oriented evidence about the completed message, including its wire bytes, payload bytes, and applicable check result. Text decoding is intentionally not performed by the library.

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

The solution contains both the library and `SerialCommunications.Tests`. The test suite exercises framing, checks, receive processing, lifecycle/recovery behavior, telemetry, and related communications semantics without depending on a GUI.

## Design boundary

SerialCommunications should remain useful to both interactive tools and unattended instrument software. In particular, it should report communications facts rather than decide application policy. It should not decide which response belongs to a request, how many responses an instrument should produce, when an application should retry a command, whether an operator should be notified, or how a protocol-specific state machine advances.

This boundary is intentional rather than an omission: those policies vary substantially between devices while the underlying serial mechanics are reusable.

## License and provenance

SerialCommunications is licensed under the GNU General Public License version 3; see `LICENSE.txt`.

The library was developed within SerialBench and extracted into this independent repository in October 2026. Earlier development history remains in the SerialBench repository, which was named `SerialPortTerminal` before the split.

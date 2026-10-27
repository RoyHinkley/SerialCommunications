# SerialCommunications project notes

> Development handoff for unresolved work and easy-to-lose constraints. Settled user-facing behavior belongs in code, tests, XML documentation, and README.md; completed history belongs in Git. Keep this file short and current rather than accumulating a development log.

## Project boundary

SerialCommunications is an independent reusable .NET serial-device communications library and is the completed communications foundation for current SerialBench development. Existing and prospective consumers are useful requirements sources, but adapting those consumers to this library is work in their own projects and is not a SerialCommunications deliverable. In particular, SerialBench's current migration from its older CRC/termination configuration model to the library's `FrameLayout`/start/end/check model is SerialBench work; do not add compatibility API merely to make that migration convenient.

The library owns serial configuration, transport lifecycle and automatic recovery, receive acquisition and framing, message checks, message pacing, diagnostics, telemetry, and faithful byte-oriented message delivery. It does not own application presentation, persistence, operator notification, or instrument-specific command/response semantics.

Keep these concepts distinct:

- connection intent;
- transport availability;
- communications health.

Malformed messages, failed checks, timeouts, and isolated I/O errors are diagnostic evidence and do not by themselves imply physical transport loss.

## Agreed semantics and constraints

- `ReceivedData` is byte-oriented evidence. Text decoding and presentation belong to consumers; consumers do not construct receive records themselves.
- `SerialChannelTelemetry.MessagesReceived` counts decoded/framed messages, including invalid messages suppressed from delivery. `CheckFailures` records the independent failed-check fact. Recovery-period attempt state remains distinct from lifetime telemetry.
- Logical transmit telemetry/trace occurs only after the complete write succeeds. Failed writes are errors but are not counted or narrated as completed transmissions.
- Event-subscriber exceptions are consumer faults, not communications errors; isolate them and report them only as detailed diagnostics.
- `TraceFlags.Detail` and `TraceFlags.Noisy` are independent diagnostic categories.
- Unexpected transport loss is recovered automatically while connection intent remains true. Intentional reconnection is expressed with `Disconnect()` / `Connect()`; communications-health failures do not implicitly mean transport loss.
- `Connect()` while already connecting or connected is rejected and reports an `Error`; `Disconnect()` is the explicit way to abandon an in-progress acquisition before requesting another connection.
- Live protocol changes apply immediately when the receive decoder has no incomplete message. If bytes are buffered, the old decoder completes that message and the staged decoder takes over exactly at its boundary, including when following bytes occur in the same transport chunk. Each transmitted command uses one complete protocol snapshot.
- Frame layouts describe wire order using S (start), P (payload), E (end), and C (check): `P`, `PE`, `PC`, `PEC`, `SP`, `SPE`, `SPC`, `SPEC`, plus the exceptional legacy `PCE`. S and E are literal byte sequences and may be empty. A C-bearing layout requires a configured check method; otherwise configured but unused framing/check values are dormant rather than errors. Checks cover P only.
- CRC-16 is the currently demonstrated generic message-check method. Additional check algorithms belong here only when a concrete reusable requirement appears.
- Receive decoder/pump/dispatch types are implementation details. Consumer dispatch is asynchronous and deliberately unbounded so the communications layer does not stall reception or silently discard messages because application code is slow. Consumers that need bounded admission impose that policy above the library. Disposal remains non-blocking with respect to arbitrary consumer code.
- Protocol-specific byte selection, bypass rules, field names, textual check representations, request/response association, retries, polling, expected-response counts, ACK/NAK interpretation, addressing, and instrument state machines belong above the generic communications layer.
- Configuration snapshots belong in the library (`SerialChannelConfiguration` / `SerialProtocolSettings`); live editing, persistence, profiles, and UI defaults belong to consumers.
- Maintain concise XML documentation where public API semantics benefit from it: contracts and invariants, units, defaults and special values, ownership/lifetime/threading behavior, and non-obvious algorithms. Do not narrate obvious code, repeat identifiers, or enable CS1591 merely to require comments on every public identifier.
- Avoid a generic conversation/protocol DSL or other speculative API unless concrete common behavior is demonstrated by multiple consumers.
- Genuinely unframed streaming is not a current requirement; add it only for a demonstrated use case.

## Remaining work before project completion

- Clean up received check evidence and failed-check diagnostics. Remove the redundant CRC-specific `ReceivedData.CheckValid` alias; rename `CrcBytes` to the generic `CheckBytes`; rename `ReceivedCrc` to the generic `ReceivedCheckCode` and use an integer representation suitable for the configured check-code width rather than exposing CRC-16 in the consumer-facing name. Retain `CalculatedResidue` and `ExpectedResidue` because they describe the CRC validation actually performed and remain useful structured evidence for consumers such as virtual instruments. On a failed check, SerialCommunications must emit a sufficiently detailed diagnostic before `SuppressInvalidMessages` can withhold the `ReceivedData`: include the check bytes in wire order, the interpreted received check code, calculated residue, and expected residue. Format the received check code as hexadecimal padded to the same byte width as the check bytes so byte-order configuration errors are visually obvious. Keep the structured values in `ReceivedData` even though consumers such as SerialBench may persist only `CheckValid` and preserve the detailed failure information through the diagnostic trace.
- Resolve incomplete explicit-end framing on receive silence. `LayoutAwareMessageDecoder.SilenceElapsed()` currently leaves pending data buffered indefinitely for ordinary layouts with a non-empty E when the expected E never arrives. `MaximumMillisecondsSilenceInMessage` is the natural timeout boundary: after that silence, pending bytes should be resolved rather than retained indefinitely, with a framing error reported and the accumulated bytes represented as an invalid message so `SuppressInvalidMessages` controls consumer delivery without losing diagnostic evidence. Define and test the exact behavior across the explicit-end layouts before implementation. This deficiency is deferred while SerialBench work proceeds; it does not block SerialBench's session-store design because raw acquisitions are already present in the Trace stream.
- Audit the library for every wait-for-condition path and require a bounded or explicitly interruptible exit. No external condition should be awaited indefinitely. Review framing/decoder waits, transport and acquisition waits, worker/thread joins, recovery delays, signal/event waits, and synchronization primitives. An intentional idle wait is acceptable only when shutdown or a relevant state change reliably interrupts it. Check state-machine behavior as well as timer/synchronization calls: the explicit-end framing defect demonstrates that a timeout can fire correctly while pending state still remains unresolved indefinitely.
- Implement RTS Toggle support. `RtsModes.Toggle` intentionally remains unsupported by the current `SerialPort` transport and is a demonstrated missing capability. Verify a supported implementation with current .NET, Windows and representative adapters; do not use reflection into private `SerialPort` internals.
- Complete physical hardware qualification on Windows and Linux. Exercise the same behavioral contract on both platforms, using known underlying implementation differences to choose stress points rather than to define different success criteria. Include connection/acquisition and teardown under active I/O, unplug/replug recovery, session isolation, configuration and signal behavior, framing/check correctness, sustained traffic/performance, and relevant timing/edge conditions. Keep hardware tests opt-in so ordinary CI remains hardware-independent.

Except for those items, the current library is considered feature-complete. New capabilities should be driven by demonstrated serial-communications requirements rather than by prospective compatibility.

## Development rules

- Work directly on `main`; use small coherent commits and re-fetch files immediately before writing.
- Keep library behavior testable without UI dependencies.
- Preserve characterized behavior while modernizing structure; correctness and explicit ownership/semantics outrank convenience.
- Avoid speculative API and naive O(N²) work where scale can matter.
- Proceed autonomously through coherent work; ask for consumer testing at useful checkpoints rather than after every commit.
- Do not claim tests ran unless they actually ran.

# SerialCommunications project notes

> Development handoff for unresolved work and easy-to-lose constraints. Settled user-facing behavior belongs in code, tests, XML documentation, and README.md; completed history belongs in Git.

## Project boundary

SerialCommunications is an independent reusable .NET serial-device communications library used by SerialBench and intended for automated consumers such as AeonHacs.

The library owns serial configuration, transport lifecycle and automatic recovery, receive acquisition and framing, message checks, message pacing, diagnostics, telemetry, and faithful byte-oriented message delivery. It does not own application presentation, persistence, operator notification, or instrument-specific command/response semantics.

Keep these concepts distinct:

- connection intent;
- transport availability;
- communications health.

Malformed messages, failed checks, timeouts, and isolated I/O errors are diagnostic evidence and do not by themselves imply physical transport loss.

## Agreed semantics and constraints

- `ReceivedData` is byte-oriented evidence. Text decoding and presentation belong to consumers; consumers do not construct receive records themselves.
- `SerialChannelTelemetry.MessagesReceived` counts decoded/framed messages, including invalid messages suppressed from delivery. `CheckFailures` records the independent failed-check fact. Recovery-period attempt state remains distinct from lifetime telemetry.
- Logical `Transmit` telemetry/trace occurs only after the complete write succeeds. Failed writes are errors but are not counted or narrated as completed transmissions.
- Event-subscriber exceptions are consumer faults, not communications errors; isolate them and report them only as detailed diagnostics.
- `TraceFlags.Detail` and `TraceFlags.Noisy` are independent diagnostic categories.
- Unexpected transport loss is recovered automatically while connection intent remains true. Intentional reconnection is expressed with `Disconnect()` / `Connect()`; communications-health failures do not implicitly mean transport loss.
- Live protocol changes apply immediately when the receive decoder has no incomplete message. If bytes are buffered, the old decoder completes that message and the staged decoder takes over exactly at its boundary, including when following bytes occur in the same transport chunk.
- Receive decoder/framer/pump/dispatch types are implementation details. Consumer dispatch is asynchronous and deliberately unbounded so the communications layer does not stall reception or silently discard messages because application code is slow. Consumers that need bounded admission impose that policy above the library. Disposal remains non-blocking with respect to arbitrary consumer code.
- Generic XOR is the zero-seeded XOR of applicable bytes. Generic additive checksum is the low eight bits of the zero-seeded byte sum. CRC-16 remains configurable. Do not expose speculative initial-accumulator configuration without a demonstrated protocol requirement.
- Protocol-specific byte selection, bypass rules, field names, textual check representations, request/response association, retries, polling, expected-response counts, ACK/NAK interpretation, addressing, and instrument state machines belong above the generic communications layer.
- Configuration snapshots belong in the library (`SerialChannelConfiguration` / `SerialProtocolSettings`); live editing, persistence, profiles, and UI defaults belong to consumers such as SerialBench.
- Maintain concise XML documentation where public API semantics benefit from it: non-obvious behavior, units, defaults, special values, lifetime/threading behavior, and exact algorithms. Do not enable CS1591 simply to require comments on every public identifier.
- Avoid a generic conversation/protocol DSL unless concrete common behavior is demonstrated by multiple consumers.

## Deferred work

- Continue concise XML documentation where public API semantics benefit from it.
- Implement RTS Toggle support. This is a required capability with an existing AeonHacs instrument use case, not speculative compatibility work. Verify behavior with current .NET/Windows and representative adapters; do not use reflection into private `SerialPort` internals.
- Add genuinely unframed streaming only for a demonstrated use case.

## Development rules

- Work directly on `main`; use small coherent commits and re-fetch files immediately before writing.
- Keep library behavior testable without Avalonia or other UI dependencies.
- Preserve characterized behavior while modernizing structure; correctness and explicit ownership/semantics outrank convenience.
- Avoid speculative API and naive O(N²) work where scale can matter.
- Proceed autonomously through coherent work; ask for consumer testing at useful checkpoints rather than after every commit.
- Do not claim tests ran unless they actually ran.

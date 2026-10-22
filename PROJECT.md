# SerialCommunications project notes

> Temporary development handoff for unresolved questions, deferred work, and easy-to-lose constraints. Settled behavior belongs in code, tests, XML documentation, and README.md; completed history belongs in Git.

## Current checkpoint

SerialCommunications is an independent reusable .NET serial-device communications library extracted from SerialBench in October 2026. The earlier development history remains in `RoyHinkley/SerialBench` (formerly `RoyHinkley/SerialPortTerminal`); provenance is summarized in README.md.

The repository now owns its library-focused test suite in `SerialCommunications.Tests`, and `InternalsVisibleTo` is limited to that test assembly. SerialBench retains its application tests. The library has its own GitHub Actions build/test workflow.

The library owns serial configuration, transport lifecycle and automatic recovery, receive acquisition and framing, message checks, message pacing, diagnostics, telemetry, and faithful byte-oriented message delivery. It does not own application presentation, persistence, operator notification, or instrument-specific command/response semantics.

Keep these concepts distinct:

- connection intent;
- transport availability;
- communications health.

Malformed messages, failed checks, timeouts, and isolated I/O errors are diagnostic evidence and do not by themselves imply physical transport loss.

## Settled semantics

- `ReceivedData` is byte-oriented evidence. Text decoding and presentation belong to consumers.
- `SerialChannelTelemetry.MessagesReceived` counts decoded/framed messages, including invalid messages suppressed from delivery. `CheckFailures` records the independent failed-check fact.
- Logical `Transmit` telemetry/trace occurs only after the complete write succeeds.
- Event-subscriber exceptions are consumer faults and are isolated as detailed diagnostics rather than channel errors.
- `TraceFlags.Detail` and `TraceFlags.Noisy` are independent diagnostic categories.
- Unexpected transport loss is recovered automatically while connection intent remains true. Intentional reconnection is expressed with `Disconnect()` / `Connect()`; there is no ambiguous generic `Reset()` operation.
- Receive decoder/framer/pump/dispatch types are implementation details. Consumer dispatch is asynchronous and deliberately unbounded so the communications layer does not silently discard messages because application code is slow.
- Generic XOR is the zero-seeded XOR of applicable bytes. Generic additive checksum is the low eight bits of the zero-seeded byte sum. CRC-16 remains configurable. Protocol-specific byte selection, bypass rules, field names, and textual representations belong above these generic calculations.
- XML documentation should explain meaningful public semantics, units, defaults, special values, lifetime/threading behavior, and exact algorithms. Do not enable CS1591 simply to require comments on every public identifier.
- Request/response orchestration remains above the library. Do not add a generic conversation/protocol DSL without concrete common behavior demonstrated by consumers.

## Post-split cleanup

- Normalize the moved test namespaces from the historical `SerialBench.Tests` namespace to `SerialCommunications.Tests`; this is cosmetic because assembly identity, not namespace, controls `InternalsVisibleTo`.
- Rename the stale `SerialDeviceRecoveryTests` and `SerialDeviceTransportLifecycleTests` files/classes to `SerialChannel...` terminology after the split is proven clean.
- Verify the independent library build and test workflow after the test-project move.

## Deferred work

- Continue concise XML documentation where public API semantics benefit from it.
- Verify RTS Toggle behavior with current .NET/Windows and representative adapters only when a target setup requires it; do not use reflection into private `SerialPort` internals.
- Add genuinely unframed streaming only for a demonstrated use case.

## Development rules

- Work directly on `main`; use small coherent commits and re-fetch files immediately before writing.
- Keep library behavior testable without Avalonia or other UI dependencies.
- Preserve characterized behavior while modernizing structure; correctness and explicit ownership/semantics outrank convenience.
- Avoid speculative API and naive O(N²) work where scale can matter.
- Do not claim tests ran unless they actually ran.

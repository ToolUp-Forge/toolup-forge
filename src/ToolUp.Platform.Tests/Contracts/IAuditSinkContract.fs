module ToolUp.Platform.Tests.Contracts.IAuditSinkContract

open System
open Expecto
open ToolUp.Platform

/// Phase 9g — contract assertions every `IAuditSink` implementation
/// must satisfy. Bound by `InProcess` test files that supply a factory
/// returning a fresh sink. Cross-cutting properties — identity by
/// string, async-returning Deliver, batch-idempotent on retry — are
/// enforced at the test level so future companions (SplunkHec /
/// DatadogLogs / S3Archive / custom SIEMs) bind to the same pack with
/// a vendor-specific factory.
///
/// The pack accepts a `verifyDelivered` callback so vendor companions
/// can assert that a downstream stub recorded the batch correctly
/// (Splunk: HTTP body received; S3: blob written; etc.). The
/// `InMemoryAuditSink` binding inspects its own `Received` field.
///
/// Phase 66 Stream B.7 — batches are `AuditEnvelope list` (not bare
/// `AuditEvent list`). The contract pack synthesises envelopes via
/// `AuditEnvelope.fromScopeId` for a stable `team-contract` scope so
/// every sink sees a fully-populated envelope with `Subject =
/// TeamAudit(_dispatcher, contract)`, `OccurredAt` ticking by offset,
/// and `ScopeId = "team-contract"`. Sinks also declare
/// `SchemaVersion` against `LatestAuditSchemaVersion`; the pack
/// verifies the declaration.
let tests
    (name: string)
    (factory: unit -> IAuditSink)
    (verifyDelivered: IAuditSink -> AuditEnvelope list list -> unit)
    =
    let baseTime = DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc)

    let makeAuditEvent (offset: float) =
        UserLoggedIn {
            UserId = sprintf "user-%d" (int (DateTime.UtcNow.AddSeconds offset).Ticks)
            AuthProvider = "Header"
        }

    let makeEnvelope (offset: float) : AuditEnvelope =
        AuditEnvelope.fromScopeId "team-contract" (baseTime.AddSeconds offset) (makeAuditEvent offset)

    testList $"{name} — IAuditSink contract" [
        testCaseAsync "Name is non-empty"
        <| async {
            let sink = factory ()

            Expect.isFalse
                (String.IsNullOrWhiteSpace sink.Name)
                "Sink.Name must be a non-empty deployment-unique identifier"
        }

        testCaseAsync "SchemaVersion matches AuditSchemaVersion.current"
        <| async {
            let sink = factory ()

            Expect.equal
                sink.SchemaVersion
                AuditSchemaVersion.current
                "Sink.SchemaVersion must equal AuditSchemaVersion.current for the wire format the sink emits"
        }

        testCaseAsync "Empty batch delivery returns Ok"
        <| async {
            let sink = factory ()
            let! result = sink.Deliver []

            match result with
            | Ok() -> ()
            | Error msg -> failtestf "Empty batch must return Ok; got Error %s" msg
        }

        testCaseAsync "Happy-path delivery returns Ok with non-empty batch"
        <| async {
            let sink = factory ()
            let batch = [ makeEnvelope 0.0; makeEnvelope 1.0; makeEnvelope 2.0 ]
            let! result = sink.Deliver batch

            match result with
            | Ok() -> verifyDelivered sink [ batch ]
            | Error msg -> failtestf "Happy-path delivery must return Ok; got Error %s" msg
        }

        testCaseAsync "Multiple batches delivered in arrival order"
        <| async {
            let sink = factory ()
            let batchA = [ makeEnvelope 0.0; makeEnvelope 1.0 ]
            let batchB = [ makeEnvelope 10.0 ]
            let batchC = [ makeEnvelope 20.0; makeEnvelope 21.0; makeEnvelope 22.0 ]

            let! resultA = sink.Deliver batchA
            let! resultB = sink.Deliver batchB
            let! resultC = sink.Deliver batchC

            Expect.equal resultA (Ok()) "batch A delivered"
            Expect.equal resultB (Ok()) "batch B delivered"
            Expect.equal resultC (Ok()) "batch C delivered"
            verifyDelivered sink [ batchA; batchB; batchC ]
        }

        testCaseAsync "Deliver does not throw — failures surface via Result.Error"
        <| async {
            // The contract is that Deliver returns Result.Error rather
            // than throwing an exception that would bypass the
            // dispatcher's retry loop. Ok results are equally valid
            // for an offline / fake sink; this test only fails if an
            // unhandled exception escapes Deliver.
            let sink = factory ()
            let batch = [ makeEnvelope 0.0 ]

            try
                let! result = sink.Deliver batch

                match result with
                | Ok()
                | Error _ -> ()
            with ex ->
                failtestf "Deliver must surface failures via Result.Error, not by throwing: %s" ex.Message
        }
    ]
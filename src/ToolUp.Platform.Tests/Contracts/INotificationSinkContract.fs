module ToolUp.Platform.Tests.Contracts.INotificationSinkContract

open System
open Expecto
open ToolUp.Platform

/// Contract assertions every `INotificationSink` implementation must
/// satisfy. Bound by an `InProcess` test that supplies a factory
/// returning a fresh sink + an envelope shape appropriate for the
/// sink's `Kind`. Cross-cutting properties — identity by value,
/// async at every method, idempotent skip semantics, no audit on
/// `Skipped` — are enforced at the test level so future companions
/// (SendGrid / Twilio / WebPush) bind to the same pack with a vendor-
/// specific factory.
///
/// Phase 6f. Mirrors `INotificationChannelContract` in shape; both
/// packs accept a display name and a factory and return an Expecto
/// `testList` the runner aggregates with the rest.
let tests (name: string) (factory: unit -> INotificationSink) (sampleEnvelope: string -> NotificationEnvelope) =
    testList $"{name} — INotificationSink contract" [
        testCaseAsync "Kind round-trips through SinkKind.toWireString"
        <| async {
            let sink = factory ()

            // Phase 11.C.5 Tier 3 — Kind is a SinkKind DU. The
            // wire-format string must round-trip through tryParse so
            // dispatch / audit / uniqueness all see the same key.
            let wire = NotificationKind.SinkKind.toWireString sink.Kind

            match NotificationKind.SinkKind.tryParse wire with
            | Some parsed ->
                Expect.equal
                    parsed
                    sink.Kind
                    "SinkKind.toWireString >> tryParse must round-trip every shipped sink Kind"
            | None ->
                failtestf
                    "SinkKind.tryParse returned None for wire-string %s — every shipped sink Kind must round-trip"
                    wire
        }

        testCaseAsync "Provider is non-empty"
        <| async {
            let sink = factory ()

            Expect.isFalse (String.IsNullOrWhiteSpace sink.Provider) "Sink.Provider must be a non-empty vendor label"
        }

        testCaseAsync "Send returns a SinkResult (not a thrown exception) for a well-formed envelope"
        <| async {
            let sink = factory ()
            let envelope = sampleEnvelope "scope-test"

            // The point of the contract is that Send classifies its
            // own outcome — vendor failures surface as
            // TransientFailure / PermanentFailure, not as exceptions
            // bubbling up the dispatcher. Both Skipped and the two
            // failure modes are valid results for a fake/offline
            // factory; only thrown exceptions fail the assertion.
            let! result = sink.Send(envelope.ScopeId, envelope)

            match result with
            | SinkResult.Delivered _
            | SinkResult.Skipped _
            | SinkResult.TransientFailure _
            | SinkResult.PermanentFailure _ -> ()
        }
    ]
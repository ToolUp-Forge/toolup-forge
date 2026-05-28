module ToolUp.Platform.Tests.InProcess.InMemoryAuditSinkTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts

/// Phase 9g — bind `IAuditSinkContract` to the SDK's in-process test
/// double. The contract pack proves the cross-cutting properties
/// (Name non-empty, empty-batch Ok, Result-not-throw) that any
/// vendor companion (Splunk HEC, Datadog Logs, S3 Archive) will also
/// satisfy. Companion-specific behaviour (HTTP body shape, blob
/// naming, vendor idempotency keys) is tested in each companion's
/// own test project.
let tests =
    let factory () =
        InMemoryAuditSink "memory-test" :> IAuditSink

    let verifyDelivered (sink: IAuditSink) (expected: AuditEnvelope list list) =
        let inMemory = sink :?> InMemoryAuditSink
        let received = inMemory.Received

        Expect.equal received expected "InMemoryAuditSink.Received must capture every envelope batch in arrival order"

    IAuditSinkContract.tests "InMemoryAuditSink" factory verifyDelivered
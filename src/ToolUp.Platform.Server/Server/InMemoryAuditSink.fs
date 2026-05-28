namespace ToolUp.Platform

open System.Collections.Concurrent

/// Phase 9g — `IAuditSink` test double. Records every delivered batch
/// into a `ConcurrentBag` so contract / integration tests can assert on
/// what reached the sink. Lives in `ToolUp.Platform/Server/` rather than
/// `ToolUp.Platform.Tests/` so the contract pack can bind it without a
/// reverse `<ProjectReference>` from the SDK to its own tests.
///
/// **Configurable failure modes** for testing dispatcher retry / dead-
/// letter behaviour:
/// - `failNextN` — fail the next N `Deliver` calls (then return to OK)
/// - `failAlways` — fail every `Deliver` call until the property flips
///
/// Both are per-instance state — tests that need a stable failure
/// pattern construct a fresh instance per test. The dispatcher is
/// expected to retry the same batch after a `Result.Error`, so a sink
/// configured with `failNextN = 3` will see the same batch four times
/// (three failures + one success) before the cursor advances.
type InMemoryAuditSink(name: string) =
    // ConcurrentQueue (not ConcurrentBag) preserves FIFO ordering so
    // the contract assertion "batches delivered in arrival order" can
    // verify the sequence the dispatcher invoked.
    let received = ConcurrentQueue<AuditEnvelope list>()
    let mutable failCounter = 0
    let mutable failAlways = false
    let mutable nextErrorMessage = "InMemoryAuditSink configured failure"

    /// Every envelope batch the sink has been asked to deliver, in
    /// arrival order. Tests assert on `Received` after exercising the
    /// dispatcher. The list-of-list shape mirrors the dispatcher's
    /// per-batch chunking — one inner list per `Deliver` call.
    member _.Received: AuditEnvelope list list = received |> Seq.toList

    /// Same shape as `Received` but projected to the inner
    /// `AuditEvent`s. Convenience for tests that don't care about the
    /// envelope's subject / occurredAt / scopeId fields.
    member this.ReceivedEvents: AuditEvent list list =
        this.Received |> List.map (List.map AuditEnvelope.event)

    /// Total events delivered (sum across all batches). Convenience for
    /// the common assertion pattern.
    member this.TotalDelivered: int = this.Received |> List.sumBy List.length

    /// Configure the sink to fail the next `n` `Deliver` calls before
    /// returning to `Result.Ok`. After the budget is exhausted the sink
    /// behaves normally.
    member _.FailNext(n: int, message: string) =
        failCounter <- n
        nextErrorMessage <- message

    /// Configure the sink to fail every `Deliver` call until
    /// `StopFailing()` is called. Used for dead-letter testing where the
    /// dispatcher must exhaust the retry budget.
    member _.FailAlways(message: string) =
        failAlways <- true
        nextErrorMessage <- message

    /// Stop the always-fail mode; subsequent deliveries succeed.
    member _.StopFailing() = failAlways <- false

    interface IAuditSink with
        member _.Name = name

        member _.SchemaVersion = AuditSchemaVersion.current

        member _.Deliver(batch) = async {
            if List.isEmpty batch then
                return Ok()
            elif failAlways then
                return Error nextErrorMessage
            elif failCounter > 0 then
                failCounter <- failCounter - 1
                return Error nextErrorMessage
            else
                received.Enqueue batch
                return Ok()
        }
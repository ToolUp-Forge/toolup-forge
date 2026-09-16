module ToolUp.Platform.Tests.InProcess.SessionStoreResetTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.FileManagement
open DataManagementTypes
open ProcessedDataTypes

// ─── Phase 6p — session-store reset transition ──────────────────
//
// Deliberately NOT under `Contracts/` and NOT named `*Contract.fs`:
// that naming declares a PARAMETRISED portability pack over an
// interface, which the conformance-coverage ratchet then expects two
// implementations to bind (GP 12 — a portable interface is unproven
// until a second implementation runs the same pack). This is neither.
// It has one concrete subject — the shipped `FileManagement` store
// registry — and there is no seam here for a second implementation to
// occupy. It sits beside `FileManagementTests`, which tests the same
// module the same way.
//
// The server half of the reconciliation: a `SessionFileStore` carries an
// epoch that changes when the store instance does, and an
// eviction-then-recreate — and ONLY that — announces itself on the
// notification channel and in the audit trail.
//
// The pack is deliberately paired. Every "it fired" case has a control
// asserting the quiet path stays quiet, because "announce on every store
// resolution" would satisfy the positive cases on its own and is a
// failure mode with the same signature as "announce on eviction": the
// client would toast and clear a healthy session's file list on every
// page load. The three baselines that pin that down are the fresh-first-
// access case, the healthy-steady-state case, and the process-restart
// case — the last being the one the DESIGN says is undetectable
// server-side, so a pack that did not assert it could not tell a correct
// implementation from one that had quietly started guessing.
//
// These tests mutate module-level state (`storeEvictionMinutes`, the
// store dictionary) that the whole pack shares, which is safe because
// every pack runs `Sequenced` by default — see
// docs/platform/testing-conventions.md. Each case restores the TTL it
// changed.

/// Records every `Record` call so a test can assert audit shape + count.
type private RecordingAuditLog() =
    let recorded = ConcurrentQueue<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Enqueue(scopeId, audit) }

        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

/// Records every `Publish` so a test can assert the notification's key,
/// payload and scope. Deliberately NOT the in-tree
/// `InMemoryNotificationChannel`: what is under test is what the
/// publisher HANDED the channel, and a channel that also delivers would
/// let a delivery bug mask a publish bug.
type private RecordingChannel() =
    let published = ConcurrentQueue<string * Notification>()
    member _.Published = List.ofSeq published

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { published.Enqueue(scopeId, notification) }

        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_) = async { return () }

/// Both emissions are fire-and-forget (`Async.Start`) so the request that
/// re-created the store is never delayed by them — which means a test
/// cannot simply read the recorder on the next line. Poll to a deadline
/// rather than sleeping a fixed span: a fast machine finishes in the
/// first iteration, and a slow one is given room without every run paying
/// for it.
///
/// A timeout returns rather than throws, so the ASSERTION reports what
/// was expected and what arrived instead of a bare "timed out".
let private waitFor (timeoutMs: int) (condition: unit -> bool) = async {
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

    let mutable satisfied = condition ()

    while not satisfied && DateTime.UtcNow < deadline do
        do! Async.Sleep 10
        satisfied <- condition ()
}

let private csv = "header_x,header_y\n1,2\n3,4\n"

let private upload fileName : DataFileUpload = {
    filename = fileName
    contents = csv
    dataType = "UnrecognisedData"
}

/// A fresh ephemeral scope per case. `Persist = false` is what makes the
/// store eligible for the TTL sweep at all — the eviction path this phase
/// reconciles against is by construction the ephemeral one.
let private freshScope () =
    let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)

    {
        ScopeId = "session-reset-" + suffix
        Container = "session-reset-" + suffix
        Persist = false
    }

type private Rig = {
    Scope: StorageScope
    Runtime: FileManagementRuntime
    Audit: RecordingAuditLog
    Channel: RecordingChannel
}

let private mkRig () =
    let audit = RecordingAuditLog()
    let channel = RecordingChannel()

    let runtime = {
        FileManagementRuntime.empty with
            AuditLog = Some(audit :> IAuditLog)
            NotificationChannel = Some(channel :> INotificationChannel)
    }

    {
        Scope = freshScope ()
        Runtime = runtime
        Audit = audit
        Channel = channel
    }

let private resolve (rig: Rig) = getStore [] None rig.Runtime rig.Scope

/// Run the eviction sweep with a zero TTL so every ephemeral store is
/// past its cutoff, then restore whatever the deployment default was.
/// `configureEvictionMinutes` deliberately refuses a non-positive value,
/// so the mutable is set directly — the same escape the phase shard
/// names.
let private evictEverything () =
    let previous = storeEvictionMinutes

    try
        storeEvictionMinutes <- 0.0
        __internal_evictNowForTests ()
    finally
        storeEvictionMinutes <- previous

let private resetNotifications (rig: Rig) =
    rig.Channel.Published
    |> List.choose (fun (scopeId, notification) ->
        match notification with
        | CustomNotification(key, json) when key = SessionStoreResetKey -> Some(scopeId, json)
        | _ -> None)

let private resetAudits (rig: Rig) =
    rig.Audit.Events
    |> List.choose (fun (scopeId, event) ->
        match event with
        | SessionStoreReset payload -> Some(scopeId, payload)
        | _ -> None)

let tests =
    testList "Phase 6p — session-store reset" [

        testCaseAsync "a store carries a stable epoch across resolutions"
        <| async {
            let rig = mkRig ()
            let first = resolve rig
            let second = resolve rig

            Expect.equal second.Epoch first.Epoch "resolving the same live store twice must not mint a new epoch"

            Expect.isTrue (first.Epoch <> Guid.Empty) "the epoch is minted at construction, not left at the default"
        }

        testCaseAsync "two distinct scopes carry distinct epochs"
        <| async {
            let a = mkRig ()
            let b = mkRig ()

            Expect.notEqual
                (resolve a).Epoch
                (resolve b).Epoch
                "epochs identify a store instance, so two stores cannot share one"
        }

        testCaseAsync "GetSessionInfo's file count tracks the store's contents"
        <| async {
            let rig = mkRig ()
            let store = resolve rig
            Expect.equal store.FileCount 0 "a fresh store holds nothing"

            let! _ = store.AddFile(upload "SalesData.csv", "tester")
            Expect.equal store.FileCount 1 "an upload is visible to the count the client corroborates the epoch with"
        }

        testCaseAsync "eviction then re-creation mints a new epoch"
        <| async {
            let rig = mkRig ()
            let before = resolve rig
            let! _ = before.AddFile(upload "SalesData.csv", "tester")

            evictEverything ()

            let after = resolve rig

            Expect.notEqual after.Epoch before.Epoch "the client's whole detection rests on this inequality"

            Expect.equal
                after.FileCount
                0
                "the re-created store is empty — which is the loss the client is being told about"
        }

        testCaseAsync "eviction then re-creation publishes Platform.SessionStoreReset with Reason = Evicted"
        <| async {
            let rig = mkRig ()
            let before = resolve rig
            let! _ = before.AddFile(upload "SalesData.csv", "tester")

            evictEverything ()
            let after = resolve rig

            do! waitFor 2000 (fun () -> not (List.isEmpty (resetNotifications rig)))

            match resetNotifications rig with
            | [ (scopeId, json) ] ->
                Expect.equal scopeId rig.Scope.ScopeId "the reset is published to the affected scope, never broadcast"

                Expect.stringContains json SessionStoreResetReasonEvicted "the payload names the cause"

                Expect.stringContains json rig.Scope.Container "the payload carries the scope container"

                Expect.stringContains json (string after.Epoch) "the payload carries the epoch the client should adopt"

                Expect.stringContains json (string before.Epoch) "the payload carries the epoch being superseded"

                // GP 4 — the payload is scope-only. The uploaded
                // filename is the nearest thing to PII in reach here,
                // and asserting its ABSENCE is what makes the principle
                // testable rather than merely stated.
                Expect.isFalse (json.Contains "SalesData.csv") "GP 4 — no filenames in the notification payload"
            | other -> failtestf "expected exactly one reset notification, got %i" (List.length other)
        }

        testCaseAsync "eviction then re-creation records exactly one SessionStoreReset audit row"
        <| async {
            let rig = mkRig ()
            let before = resolve rig
            let! _ = before.AddFile(upload "SalesData.csv", "tester")

            evictEverything ()
            resolve rig |> ignore
            // A second resolution after the marker was consumed must not
            // produce a second row — the marker is one-shot, and an
            // audit trail that double-counts a loss is as misleading as
            // one that misses it.
            resolve rig |> ignore

            do! waitFor 2000 (fun () -> not (List.isEmpty (resetAudits rig)))

            match resetAudits rig with
            | [ (scopeId, payload) ] ->
                Expect.equal scopeId rig.Scope.ScopeId "recorded under the affected scope"
                Expect.equal payload.Container rig.Scope.Container "carries the scope container"
                Expect.equal payload.Reason SessionStoreResetReasonEvicted "carries the eviction reason"
            | other -> failtestf "expected exactly one SessionStoreReset audit row, got %i" (List.length other)
        }

        testCaseAsync "BASELINE — a fresh first access announces nothing"
        <| async {
            let rig = mkRig ()
            resolve rig |> ignore

            // Give the fire-and-forget emissions the same window the
            // positive cases get. A negative assertion taken
            // immediately would pass on a slow emission rather than on
            // an absent one.
            do! Async.Sleep 200

            Expect.isEmpty (resetNotifications rig) "nothing was lost on a first access, and no client is listening"

            Expect.isEmpty (resetAudits rig) "a first access is not a data-loss event"
        }

        testCaseAsync "BASELINE — a healthy session sees zero reconciliation traffic"
        <| async {
            let rig = mkRig ()
            let store = resolve rig
            let! _ = store.AddFile(upload "SalesData.csv", "tester")

            // The steady state: the client polls its epoch on mount, on
            // focus and on reconnect, each of which resolves the store.
            for _ in 1..5 do
                resolve rig |> ignore

            do! Async.Sleep 200

            Expect.isEmpty (resetNotifications rig) "a store that was never evicted must never announce a reset"

            Expect.isEmpty (resetAudits rig) "nor record one"
            Expect.equal (resolve rig).FileCount 1 "and the uploaded file is still there"
        }

        testCaseAsync "BASELINE — a process restart mints a new epoch and announces nothing"
        <| async {
            let rig = mkRig ()
            let before = resolve rig
            let! _ = before.AddFile(upload "SalesData.csv", "tester")

            // A restart takes the dictionary AND the eviction markers
            // with it, so the server cannot know a store was ever there.
            __internal_simulateProcessRestartForTests ()

            let after = resolve rig
            do! Async.Sleep 200

            Expect.notEqual
                after.Epoch
                before.Epoch
                "the epoch still changes — this is the signal the client reconciles on"

            Expect.isEmpty
                (resetNotifications rig)
                "there is no marker to detect the restart by, and no live SSE connection to announce it over"

            Expect.isEmpty (resetAudits rig) "the server cannot record a transition it has no evidence of"
        }

        testCaseAsync "a deployment with no channel still records the audit row"
        <| async {
            // GP 13's asymmetry, made testable: opting out of the
            // notification (or composing no channel at all) must not
            // silence the audit trail. Audit answers a compliance
            // question about data loss; it is not a UX preference.
            let audit = RecordingAuditLog()

            let runtime = {
                FileManagementRuntime.empty with
                    AuditLog = Some(audit :> IAuditLog)
                    NotificationChannel = None
            }

            let scope = freshScope ()
            let before = getStore [] None runtime scope
            let! _ = before.AddFile(upload "SalesData.csv", "tester")

            evictEverything ()
            getStore [] None runtime scope |> ignore

            do! waitFor 2000 (fun () -> not (List.isEmpty audit.Events))

            let rows =
                audit.Events
                |> List.choose (fun (_, event) ->
                    match event with
                    | SessionStoreReset payload -> Some payload
                    | _ -> None)

            Expect.equal (List.length rows) 1 "the audit emission is independent of the notification"
        }

        testCaseAsync "a deployment with neither channel nor audit log evicts without throwing"
        <| async {
            // The test-harness path, and the shape a consumer that
            // bypasses `compose` gets. The announcement arms must be
            // no-ops rather than null dereferences — an eviction that
            // threw here would surface as a failed upload on whichever
            // request happened to re-create the store.
            let runtime = FileManagementRuntime.empty
            let scope = freshScope ()
            let before = getStore [] None runtime scope
            let! _ = before.AddFile(upload "SalesData.csv", "tester")

            evictEverything ()
            let after = getStore [] None runtime scope

            Expect.notEqual after.Epoch before.Epoch "the epoch moves whether or not anyone is listening"
        }
    ]
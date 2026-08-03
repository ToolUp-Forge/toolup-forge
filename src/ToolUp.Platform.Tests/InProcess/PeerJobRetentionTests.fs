module ToolUp.Platform.Tests.InProcess.PeerJobRetentionTests

open System
open System.Text
open Expecto
open ToolUp.InterPlatform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 316 — peer job-result retention / TTL ─────────────────────
//
// `BlobPeerJobResultStore` used to write one document per finished
// long-running peer call and never delete it: unbounded storage growth,
// and — because those documents hold the *typed* federated result — an
// unbounded data-retention window for a clean-room federation. Retention
// is now a `PeerJobRetentionPolicy` value the store honours lazily on
// read: an expired document reads as absent and its blob is reclaimed in
// the same call.
//
// The clock is injected (`?now`), so every horizon here is exact rather
// than slept for — a TTL test that waits out a real timespan is either
// slow or flaky, and usually both.

let private container = "_platform"

let private blobNameFor (jobId: Guid) = $"peers/jobs/_platform/{jobId}.json"

/// A hand-cranked clock. Retention is enforced against `clock ()` at both
/// write and read, so advancing it between calls is exactly what a real
/// deployment's wall clock does between a peer's poll attempts.
type private TestClock(start: DateTimeOffset) =
    let mutable current = start
    member _.Now = current
    member _.Advance(delta: TimeSpan) = current <- current.Add delta
    /// The shape the store's `?now` seam takes — a thunk read per call.
    member this.Read() : DateTimeOffset = this.Now

let private epoch = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

let private completed (value: int) =
    PeerJobStatus.Completed(JsonRpc.serialize value)

/// Store + blobs + clock, wired together. The blobs are returned too so a
/// test can assert the document is genuinely *gone*, not merely filtered
/// out of the read — reclamation is half of what this phase buys.
let private storeWith (policy: PeerJobRetentionPolicy) =
    let blobs = InMemoryBlobStorage() :> IBlobStorage
    let clock = TestClock(epoch)
    let store = BlobPeerJobResultStore(blobs, policy, clock.Read) :> IPeerJobResultStore
    store, blobs, clock

let tests =
    testList "Phase 316 peer job-result retention" [

        // ─── The declared policy is data on the seam ──────────────

        testCase "the store reports the retention policy it honours"
        <| fun _ ->
            let store, _, _ =
                storeWith (PeerJobRetentionPolicy.withTtl (TimeSpan.FromHours 2.0) PeerJobRetentionPolicy.keepForever)

            Expect.equal
                store.Retention.Ttl
                (Some(TimeSpan.FromHours 2.0))
                "Retention is readable off the seam, so a distributed store declares the same contract"

        testCase "the default policy is a generous bound, not unbounded"
        <| fun _ ->
            Expect.equal
                PeerJobRetentionPolicy.default'.Ttl
                (Some(TimeSpan.FromDays 30.0))
                "an unconfigured deployment still stops accumulating results"

            Expect.isFalse
                PeerJobRetentionPolicy.default'.DeleteOnRead
                "delete-on-read is opt-in — the default never retires a result a caller has not finished with"

            Expect.isNone PeerJobRetentionPolicy.keepForever.Ttl "keepForever is the pre-316 escape hatch"

        // ─── TTL ──────────────────────────────────────────────────

        testCaseAsync "a result past its TTL is not returned, and its blob is reclaimed"
        <| async {
            let policy =
                PeerJobRetentionPolicy.withTtl (TimeSpan.FromHours 1.0) PeerJobRetentionPolicy.keepForever

            let store, blobs, clock = storeWith policy
            let jobId = Guid.NewGuid()

            do! store.SaveResult("_platform", jobId, "buyer", completed 42)

            let! fresh = store.TryGetResult("_platform", jobId)
            Expect.isSome fresh "inside the TTL the result is still pollable"

            clock.Advance(TimeSpan.FromHours 2.0)

            let! stale = store.TryGetResult("_platform", jobId)
            Expect.isNone stale "past the TTL the result reads as absent"

            let! exists = blobs.Exists(container, blobNameFor jobId)
            Expect.isFalse exists "the expired document is deleted on the read that found it expired"
        }

        testCaseAsync "the default retention keeps a just-completed result pollable"
        <| async {
            let store, _, clock = storeWith PeerJobRetentionPolicy.default'
            let jobId = Guid.NewGuid()

            do! store.SaveResult("_platform", jobId, "buyer", completed 7)

            let! immediate = store.TryGetResult("_platform", jobId)

            match immediate with
            | Some r ->
                Expect.equal r.OwnerPeerId "buyer" "the owner stamp survives the retention envelope"
                Expect.equal r.Status (completed 7) "the typed result survives the retention envelope"
            | None -> failtest "the default retention retired a result that had only just completed"

            // Far beyond any plausible poll loop, far inside the bound.
            clock.Advance(TimeSpan.FromDays 7.0)
            let! week = store.TryGetResult("_platform", jobId)
            Expect.isSome week "a week is still comfortably inside the default 30-day bound"
        }

        testCaseAsync "keepForever retains a result past any horizon"
        <| async {
            let store, _, clock = storeWith PeerJobRetentionPolicy.keepForever
            let jobId = Guid.NewGuid()

            do! store.SaveResult("_platform", jobId, "buyer", completed 1)
            clock.Advance(TimeSpan.FromDays 3650.0)

            let! result = store.TryGetResult("_platform", jobId)
            Expect.isSome result "keepForever reproduces the pre-316 behaviour exactly"
        }

        // ─── Delete-on-read + grace window ────────────────────────

        testCaseAsync "an in-window retried poll still resolves under delete-on-read"
        <| async {
            let policy =
                PeerJobRetentionPolicy.keepForever
                |> PeerJobRetentionPolicy.withDeleteOnRead (TimeSpan.FromMinutes 5.0)

            let store, _, clock = storeWith policy
            let jobId = Guid.NewGuid()

            do! store.SaveResult("_platform", jobId, "buyer", completed 9)

            let! first = store.TryGetResult("_platform", jobId)
            Expect.isSome first "the first poll resolves"

            clock.Advance(TimeSpan.FromMinutes 1.0)
            let! retried = store.TryGetResult("_platform", jobId)
            Expect.isSome retried "a retried poll inside the grace window still resolves"

            clock.Advance(TimeSpan.FromMinutes 1.0)
            let! again = store.TryGetResult("_platform", jobId)
            Expect.isSome again "the grace window does not slide forward on each read"
        }

        testCaseAsync "delete-on-read removes the blob after the grace window"
        <| async {
            let policy =
                PeerJobRetentionPolicy.keepForever
                |> PeerJobRetentionPolicy.withDeleteOnRead (TimeSpan.FromMinutes 5.0)

            let store, blobs, clock = storeWith policy
            let jobId = Guid.NewGuid()

            do! store.SaveResult("_platform", jobId, "buyer", completed 9)

            let! first = store.TryGetResult("_platform", jobId)
            Expect.isSome first "the first poll resolves and starts the grace clock"

            clock.Advance(TimeSpan.FromMinutes 6.0)

            let! swept = store.TryGetResult("_platform", jobId)
            Expect.isNone swept "past the grace window the result reads as absent"

            let! exists = blobs.Exists(container, blobNameFor jobId)
            Expect.isFalse exists "the swept document is reclaimed, not merely hidden"
        }

        testCaseAsync "a never-read delete-on-read result is still bounded by the TTL"
        <| async {
            let policy =
                PeerJobRetentionPolicy.keepForever
                |> PeerJobRetentionPolicy.withTtl (TimeSpan.FromHours 1.0)
                |> PeerJobRetentionPolicy.withDeleteOnRead (TimeSpan.FromDays 1.0)

            let store, _, clock = storeWith policy
            let jobId = Guid.NewGuid()

            do! store.SaveResult("_platform", jobId, "buyer", completed 5)
            clock.Advance(TimeSpan.FromHours 2.0)

            let! result = store.TryGetResult("_platform", jobId)
            Expect.isNone result "delete-on-read is lazy, so the TTL is the backstop for a result nobody polls"
        }

        // ─── Pre-316 documents ────────────────────────────────────

        testCaseAsync "a document written before retention existed is kept, not retired"
        <| async {
            // The exact bytes the pre-316 store wrote: a `PeerJobRecord`
            // with neither stamp. Both absent fields must deserialise to
            // `None`, or an upgrade would silently discard every
            // in-flight federated result on the first poll after deploy.
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let clock = TestClock(epoch)
            let jobId = Guid.NewGuid()

            let legacy: PeerJobRecord = {
                OwnerPeerId = "buyer"
                Status = completed 11
            }

            let! _ = blobs.Upload(container, blobNameFor jobId, Encoding.UTF8.GetBytes(JsonRpc.serialize legacy))

            let store =
                BlobPeerJobResultStore(blobs, PeerJobRetentionPolicy.default', clock.Read) :> IPeerJobResultStore

            clock.Advance(TimeSpan.FromDays 365.0)

            let! result = store.TryGetResult("_platform", jobId)

            match result with
            | Some r -> Expect.equal r.Status (completed 11) "the pre-316 document still round-trips its typed result"
            | None -> failtest "the upgrade retired a document that carried no expiry stamp"
        }

        // ─── Absence stays indistinguishable ──────────────────────

        testCaseAsync "an expired result and a job that never existed both read as absent"
        <| async {
            let policy =
                PeerJobRetentionPolicy.withTtl (TimeSpan.FromHours 1.0) PeerJobRetentionPolicy.keepForever

            let store, _, clock = storeWith policy
            let expired = Guid.NewGuid()
            let neverExisted = Guid.NewGuid()

            do! store.SaveResult("_platform", expired, "buyer", completed 3)
            clock.Advance(TimeSpan.FromHours 2.0)

            let! a = store.TryGetResult("_platform", expired)
            let! b = store.TryGetResult("_platform", neverExisted)

            Expect.equal
                a
                b
                "expired and never-existed are the same answer — possession of a jobId discloses nothing (Phase 308)"
        }
    ]
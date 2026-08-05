module ToolUp.Platform.Tests.InProcess.DataObjectOrphanSweepTests

open System
open System.Collections.Concurrent
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectOrphanSweep

// ─── Phase 7c — data-object orphan-blob sweep ────────────────────────
//
// `DataObjectStore.Save` writes the content blob first and the metadata
// blob second. Kill the process in between and `objects/_content/{hash}
// .data` is stranded forever: the in-band orphan GC only runs on
// `Delete` / `Evict` / `Erase`, and the object whose save died was never
// created, so nothing is ever deleted. These tests pin the whole loop —
// produce a real orphan through the real `Save` path, then reclaim it.
//
// The two arms that must be able to go red independently:
//
//   * **The grace window.** A content blob with no metadata is
//     indistinguishable from an in-flight `Save`. Deleting eagerly is a
//     data-loss bug, not a cleanup. `grace window defers` is the control
//     for `past the grace window reclaims` — same store, same sweep, one
//     stamp changed, opposite outcome. Drop the grace filter and the
//     first goes red while the second stays green.
//   * **The reverse reference.** `live content is never reclaimed` uses
//     a blob back-dated far past the grace window that a surviving
//     `v1.json` still names. Drop the metadata cross-check and only that
//     case goes red — which is the case whose failure mode is silent
//     corruption of every version pointing at the hash.
//
// The kill-mid-`Save` simulation is a store whose content write succeeds
// and whose metadata write is refused. That is the same observable state
// a `kill -9` between the two `Upload` calls leaves behind, reached
// without racing a real process teardown.

/// `IBlobStorage` double with a settable per-blob `LastModified` (so a
/// grace window is testable without waiting a day) and two injectable
/// failpoints (so the crash-mid-`Save` and refused-delete paths are
/// reachable). Deliberately local to this pack rather than a widening of
/// the shared `InMemoryBlobStorage` — the failpoints exist for exactly
/// this phase and nothing else should inherit them.
type private AgeStampedBlobStorage() =
    let blobs = ConcurrentDictionary<string * string, byte[]>()
    let stamps = ConcurrentDictionary<string * string, DateTime>()

    /// When set, `Upload` refuses any blob name matching this predicate.
    /// Used to sever `Save` between its content write and its metadata
    /// write.
    member val RefuseUpload: (string -> bool) = (fun _ -> false) with get, set

    /// When set, `Delete` refuses any blob name matching this predicate.
    member val RefuseDelete: (string -> bool) = (fun _ -> false) with get, set

    /// Phase 634 — runs immediately after a successful `Upload`, before
    /// control returns to the writer. This is the seam the `Delete`/
    /// `Save` race test needs: a `Save`'s content write has landed and
    /// its metadata write has not yet been issued, and the hook runs a
    /// concurrent `Delete` in exactly that gap. Deterministic by
    /// construction — no thread scheduling to lose.
    member val OnUploaded: (string -> unit) = (fun _ -> ()) with get, set

    member _.Names(container: string) =
        blobs.Keys
        |> Seq.filter (fun (c, _) -> c = container)
        |> Seq.map snd
        |> List.ofSeq

    /// Back-date a blob's last-write time. The sweep measures the grace
    /// window against exactly this value.
    member _.Backdate(container: string, blobName: string, at: DateTime) = stamps[(container, blobName)] <- at

    interface IBlobStorage with
        member this.Erase(container, prefix, policy, dryRun) =
            ToolUp.Platform.BlobStorage.eraseByPrefix (this :> IBlobStorage) container prefix policy dryRun

        member this.Upload(container, blobName, content) = async {
            if this.RefuseUpload blobName then
                return Error $"refused (test failpoint): {container}/{blobName}"
            else
                blobs[(container, blobName)] <- content
                stamps[(container, blobName)] <- DateTime.UtcNow
                this.OnUploaded blobName
                return Ok blobName
        }

        member _.Download(container, blobName) = async {
            match blobs.TryGetValue((container, blobName)) with
            | true, bytes -> return Ok bytes
            | false, _ -> return Error $"not found: {container}/{blobName}"
        }

        member this.DownloadRange(container, blobName, offset, length) =
            ToolUp.Platform.BlobStorage.downloadRangeViaDownload (this :> IBlobStorage) container blobName offset length

        member this.Delete(container, blobName) = async {
            if this.RefuseDelete blobName then
                return Error $"refused (test failpoint): {container}/{blobName}"
            else
                blobs.TryRemove((container, blobName)) |> ignore
                stamps.TryRemove((container, blobName)) |> ignore
                return Ok()
        }

        member _.List(container, prefix) = async {
            return
                blobs.Keys
                |> Seq.filter (fun (c, n) -> c = container && n.StartsWith prefix)
                |> Seq.map snd
                |> List.ofSeq
        }

        member _.Exists(container, blobName) = async { return blobs.ContainsKey((container, blobName)) }

        member _.GetMetadata(container, blobName) = async {
            match blobs.TryGetValue((container, blobName)) with
            | true, bytes ->
                let stamp =
                    match stamps.TryGetValue((container, blobName)) with
                    | true, at -> at
                    | false, _ -> DateTime.UtcNow

                return
                    Ok {
                        Size = int64 bytes.Length
                        LastModified = stamp
                        ContentType = None
                    }
            | false, _ -> return Error $"not found: {container}/{blobName}"
        }

/// Thread-safe capturing `IAuditLog` double.
type private CapturingAuditLog() =
    let events = ResizeArray<string * AuditEvent>()
    let gate = obj ()

    member _.Events = lock gate (fun () -> events |> List.ofSeq)

    member this.OfKind(name: string) =
        this.Events |> List.filter (fun (_, e) -> AuditEvent.eventTypeName e = name)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { lock gate (fun () -> events.Add((scopeId, audit))) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private scope = "team-orphan"
let private container = DataObjectStore.containerFor scope

let private bytesOf (s: string) = Text.Encoding.UTF8.GetBytes s

/// Drive a real `IDataObjectStore.Save` whose metadata write is refused,
/// leaving exactly the residue a kill between the two writes leaves.
/// Returns the content-blob name that was stranded.
let private strandContent (storage: AgeStampedBlobStorage) (objectId: string) (content: byte[]) : Async<string> = async {
    let store = DataObjectStore.DataObjectStore(storage, noopLogger) :> IDataObjectStore
    storage.RefuseUpload <- fun name -> name.EndsWith ".json"

    let! result = store.Save(scope, objectId, content, "text/plain", "user-1", Map.empty, Unversioned)

    storage.RefuseUpload <- fun _ -> false

    Expect.isError result "Save must fail when its metadata write is refused"

    let orphans =
        storage.Names container |> List.filter (fun n -> n.Contains "_content/")

    Expect.hasLength orphans 1 "the content blob is written before the metadata blob, so it survives the failure"

    return List.head orphans
}

let private sweep (storage: IBlobStorage) audit now grace =
    sweepScope
        storage
        audit
        noopLogger
        now
        (DataObjectOrphanSweepPolicy.forScopes [ scope ]
         |> DataObjectOrphanSweepPolicy.withOrphanSweepGracePeriod grace)
        scope
    |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "Phase 7c — data-object orphan-blob sweep" [

        test "a Save killed between its content and metadata writes strands an orphan" {
            let storage = AgeStampedBlobStorage()

            let stranded =
                strandContent storage "obj-1" (bytesOf "payload") |> Async.RunSynchronously

            let orphans =
                DataObjectStore.listOrphanedContent storage container |> Async.RunSynchronously

            Expect.hasLength orphans 1 "the stranded content blob has no metadata referencing it"
            Expect.equal (List.head orphans).BlobName stranded "the listed orphan is the stranded blob"

            Expect.equal
                (List.head orphans).SizeBytes
                (int64 (bytesOf "payload").Length)
                "the orphan carries the size the store reported"
        }

        test "an orphan inside the grace window is deferred, not reclaimed" {
            let storage = AgeStampedBlobStorage()

            let stranded =
                strandContent storage "obj-1" (bytesOf "payload") |> Async.RunSynchronously

            let audit = CapturingAuditLog()

            // Written now; grace is 24h. This is the in-flight-`Save`
            // case — the sweep must not touch it.
            let report =
                sweep storage (Some(audit :> IAuditLog)) DateTime.UtcNow (TimeSpan.FromHours 24.0)

            Expect.equal report.OrphansFound 1 "the orphan is found"
            Expect.isEmpty report.Reclaimed "nothing is reclaimed inside the grace window"
            Expect.equal report.DeferredByGrace 1 "it is deferred, and the report says so"

            Expect.contains (storage.Names container) stranded "the blob is still there"

            Expect.isEmpty audit.Events "a run that reclaimed nothing writes no audit row"
        }

        test "an orphan past the grace window is reclaimed and emits one reclaim audit event" {
            let storage = AgeStampedBlobStorage()

            let stranded =
                strandContent storage "obj-1" (bytesOf "payload") |> Async.RunSynchronously

            let now = DateTime.UtcNow
            storage.Backdate(container, stranded, now - TimeSpan.FromHours 25.0)

            let audit = CapturingAuditLog()
            let report = sweep storage (Some(audit :> IAuditLog)) now (TimeSpan.FromHours 24.0)

            Expect.hasLength report.Reclaimed 1 "the orphan is reclaimed once past the grace window"
            Expect.equal report.DeferredByGrace 0 "nothing was deferred"
            Expect.isEmpty report.Failures "the delete succeeded"

            Expect.isFalse (storage.Names container |> List.contains stranded) "the blob is gone from the backing store"

            match audit.OfKind "OrphanedContentBlobReclaimed" with
            | [ (auditedScope, OrphanedContentBlobReclaimed payload) ] ->
                Expect.equal auditedScope scope "recorded under the swept scope (GP 4)"
                Expect.equal payload.ScopeId scope "the payload names the scope"

                Expect.equal payload.ContentHash (List.head report.Reclaimed) "the payload names the reclaimed hash"

                Expect.equal payload.Bytes (int64 (bytesOf "payload").Length) "the payload carries the size"
                Expect.equal payload.AgeHours 25L "the payload carries the age that justified the reclaim"
            | other -> failtestf "expected exactly one OrphanedContentBlobReclaimed row, got %A" other

            match audit.OfKind "OrphanSweepCompleted" with
            | [ (_, OrphanSweepCompleted summary) ] ->
                Expect.equal summary.ReclaimedCount 1 "the summary counts the reclaim"
                Expect.equal summary.OrphansFound 1 "the summary counts what it found"
                Expect.equal summary.DeferredCount 0 "nothing was deferred"
                Expect.equal summary.GracePeriodHours 24L "the summary carries the policy that produced it"
                Expect.equal summary.FailureCount 0 "no failures"
            | other -> failtestf "expected exactly one OrphanSweepCompleted row, got %A" other
        }

        test "content a surviving version still references is never reclaimed, however old" {
            let storage = AgeStampedBlobStorage()
            let store = DataObjectStore.DataObjectStore(storage, noopLogger) :> IDataObjectStore

            // A COMPLETE save — content plus metadata.
            let saved =
                store.Save(scope, "obj-live", bytesOf "live", "text/plain", "user-1", Map.empty, Unversioned)
                |> Async.RunSynchronously

            Expect.isOk saved "the complete save succeeds"

            let now = DateTime.UtcNow

            // Back-date everything far past the grace window: age alone
            // must never make live content reclaimable.
            for name in storage.Names container do
                storage.Backdate(container, name, now - TimeSpan.FromDays 400.0)

            let audit = CapturingAuditLog()
            let report = sweep storage (Some(audit :> IAuditLog)) now (TimeSpan.FromHours 24.0)

            Expect.equal report.OrphansFound 0 "referenced content is not an orphan"
            Expect.isEmpty report.Reclaimed "nothing is reclaimed"
            Expect.isEmpty audit.Events "no audit rows"

            Expect.isTrue
                (storage.Names container |> List.exists (fun n -> n.Contains "_content/"))
                "the live content blob survives"
        }

        test "the sweep is idempotent — a second run reclaims nothing and writes no rows" {
            let storage = AgeStampedBlobStorage()

            let stranded =
                strandContent storage "obj-1" (bytesOf "payload") |> Async.RunSynchronously

            let now = DateTime.UtcNow
            storage.Backdate(container, stranded, now - TimeSpan.FromHours 25.0)

            let audit = CapturingAuditLog()
            sweep storage (Some(audit :> IAuditLog)) now (TimeSpan.FromHours 24.0) |> ignore
            let firstRunRows = audit.Events.Length

            let second = sweep storage (Some(audit :> IAuditLog)) now (TimeSpan.FromHours 24.0)

            Expect.equal second.OrphansFound 0 "the second run finds nothing"
            Expect.isEmpty second.Reclaimed "and reclaims nothing"
            Expect.equal audit.Events.Length firstRunRows "and writes no further audit rows"
        }

        test "GP 4 — one run reaches exactly one scope's container" {
            let storage = AgeStampedBlobStorage()

            let stranded =
                strandContent storage "obj-1" (bytesOf "payload") |> Async.RunSynchronously

            let now = DateTime.UtcNow
            storage.Backdate(container, stranded, now - TimeSpan.FromDays 30.0)

            // Sweep a DIFFERENT scope. The orphan above is past every
            // grace window; only the container boundary protects it.
            let other =
                sweepScope
                    storage
                    None
                    noopLogger
                    now
                    (DataObjectOrphanSweepPolicy.forScopes [ "team-other" ])
                    "team-other"
                |> Async.RunSynchronously

            Expect.equal other.Container "team-other" "the sweep resolved the other scope's container"
            Expect.equal other.OrphansFound 0 "it saw nothing of the first scope"
            Expect.contains (storage.Names container) stranded "the first scope's orphan is untouched"
        }

        test "a refused delete is reported as a retryable failure and the blob stays" {
            let storage = AgeStampedBlobStorage()

            let stranded =
                strandContent storage "obj-1" (bytesOf "payload") |> Async.RunSynchronously

            let now = DateTime.UtcNow
            storage.Backdate(container, stranded, now - TimeSpan.FromHours 25.0)
            storage.RefuseDelete <- fun _ -> true

            let audit = CapturingAuditLog()
            let report = sweep storage (Some(audit :> IAuditLog)) now (TimeSpan.FromHours 24.0)

            Expect.hasLength report.Failures 1 "the refusal is reported"
            Expect.isEmpty report.Reclaimed "nothing counts as reclaimed"
            Expect.isFalse (OrphanSweepReport.isClean report) "the run is not clean, so the handler retries"
            Expect.contains (storage.Names container) stranded "the blob is still there for the next run"
            Expect.isEmpty audit.Events "a reclaim row is written only for a delete that actually happened"
        }

        test "the grace window is clamped upward — a zero grace is refused" {
            let clamped =
                DataObjectOrphanSweepPolicy.disabled
                |> DataObjectOrphanSweepPolicy.withOrphanSweepGracePeriod TimeSpan.Zero

            Expect.equal
                clamped.GracePeriod
                DataObjectOrphanSweepPolicy.MinimumGracePeriod
                "a zero grace window would race live Saves, so it is clamped"

            let honoured =
                DataObjectOrphanSweepPolicy.disabled
                |> DataObjectOrphanSweepPolicy.withOrphanSweepGracePeriod (TimeSpan.FromDays 7.0)

            Expect.equal honoured.GracePeriod (TimeSpan.FromDays 7.0) "a longer grace window is honoured verbatim"

            let rescheduled =
                DataObjectOrphanSweepPolicy.disabled
                |> DataObjectOrphanSweepPolicy.withOrphanSweepSchedule "30 4 * * *"

            Expect.equal rescheduled.Schedule "30 4 * * *" "the cadence is overridable"

            Expect.equal
                DataObjectOrphanSweepPolicy.disabled.Schedule
                DataObjectOrphanSweepPolicy.DefaultSchedule
                "the default cadence is 02:00 UTC daily"

            Expect.isTrue (DataObjectOrphanSweepPolicy.isInert DataObjectOrphanSweepPolicy.disabled) "no scopes = inert"

            Expect.isFalse
                (DataObjectOrphanSweepPolicy.isInert (DataObjectOrphanSweepPolicy.forScopes [ scope ]))
                "a scope list makes it live"
        }
    ]

// ─── Phase 634 — the in-band GC's grace window ───────────────────────
//
// `collectOrphanedContent` runs on the tail of `Delete` / `Evict` /
// `Erase`. Until Phase 634 it reclaimed every unreferenced content blob
// immediately, which is safe only for the blobs the caller itself just
// released. A concurrent `Save` in the same scope, between its content
// write and its metadata write, presents an unreferenced blob that is
// indistinguishable from a crash residue — and reclaiming it loses LIVE
// content: the writer's `Save` returns `Ok`, and every later read of the
// object fails with `StorageFailure`, forever, with nothing having
// errored at write time.
//
// The rule is a UNION, and each half needs its own arm:
//
//   * **The race.** `does not destroy a concurrent Save's content`
//     drives the interleave through the real `Save` path via the
//     `OnUploaded` seam. Remove the window and it goes red — it is the
//     only case in this pack that a live writer loses data.
//   * **The released set.** `content this operation released is
//     reclaimed immediately, however fresh` is the other half, and it is
//     not a nicety: an erasure's guarantee is bytes-gone-at-rest, not
//     merely dereferenced (Phase 105 pins it on `Delete` and `Erase`).
//     A window applied to EVERYTHING would defer those to a scheduled
//     sweep a deployment may never have composed — trading a data-loss
//     race for a GDPR regression. Widen the window to all candidates and
//     this goes red while the race arm stays green.
//   * **The control.** `still reclaims a genuinely-old unreferenced
//     orphan` uses a crash-residue blob no operation released, so it
//     exercises the AGE arm rather than the released-set arm. It must
//     SURVIVE the window mutation — a "fix" that stopped reclaiming
//     would be caught here.
//   * **The boundary.** `the in-band window is the sweep's published
//     minimum` pins the duplicated constant to
//     `DataObjectOrphanSweepPolicy.MinimumGracePeriod`, which is the
//     only thing keeping the two definitions from drifting apart (the
//     store compiles ~327 files before the policy and cannot name it).

/// Complete a real `Save` and return the container-relative name of the
/// content blob it wrote.
let private saveComplete (storage: AgeStampedBlobStorage) (objectId: string) (content: byte[]) : string =
    let store = DataObjectStore.DataObjectStore(storage, noopLogger) :> IDataObjectStore

    let before = storage.Names container |> Set.ofList

    store.Save(scope, objectId, content, "text/plain", "user-1", Map.empty, Unversioned)
    |> Async.RunSynchronously
    |> fun r -> Expect.isOk r $"the complete save of {objectId} succeeds"

    storage.Names container
    |> List.filter (fun n -> n.Contains "_content/" && not (before.Contains n))
    |> function
        | [ one ] -> one
        | other -> failtestf "expected exactly one new content blob for %s, got %A" objectId other

[<Tests>]
let inBandGraceTests =
    testList "Phase 634 — in-band orphan GC grace window" [

        test "the in-band GC does not destroy a concurrent Save's content" {
            let storage = AgeStampedBlobStorage()
            let store = DataObjectStore.DataObjectStore(storage, noopLogger) :> IDataObjectStore

            // A: a complete object whose content is well past the window,
            // so its reclamation is the pre-634 behaviour and must still
            // happen. B's content is distinct, so the two never dedup
            // onto one blob.
            let aContent = saveComplete storage "obj-a" (bytesOf "a-content")
            storage.Backdate(container, aContent, DateTime.UtcNow - TimeSpan.FromHours 1.0)

            // The interleave: the moment B's content write lands, a
            // Delete(A) runs — inside B's content→metadata gap, which is
            // the whole window this phase exists to close.
            let mutable interleaved = false

            storage.OnUploaded <-
                fun name ->
                    if not interleaved && name.Contains "_content/" then
                        interleaved <- true

                        store.Delete(scope, "obj-a")
                        |> Async.RunSynchronously
                        |> fun r -> Expect.isOk r "the racing delete itself succeeds"

            let saved =
                store.Save(scope, "obj-b", bytesOf "b-content", "text/plain", "user-1", Map.empty, Unversioned)
                |> Async.RunSynchronously

            storage.OnUploaded <- fun _ -> ()

            Expect.isTrue interleaved "the delete really did land inside the gap"
            Expect.isOk saved "B's save reports success"

            // The assertion that matters: B's bytes are readable. Before
            // Phase 634 this failed with StorageFailure — the metadata
            // survived and pointed at a hash whose blob had been GC'd.
            match store.Get(scope, "obj-b") |> Async.RunSynchronously with
            | Ok(_, bytes) -> Expect.equal bytes (bytesOf "b-content") "B's content survives the racing delete"
            | Error e -> failtestf "B's content was destroyed by the racing in-band GC: %A" e

            // …and the GC was not merely inert: A's old orphan went.
            Expect.isFalse
                (storage.Names container |> List.contains aContent)
                "the racing delete still reclaimed A's own content, which was outside the window"
        }

        test "content this operation released is reclaimed immediately, however fresh" {
            let storage = AgeStampedBlobStorage()
            let store = DataObjectStore.DataObjectStore(storage, noopLogger) :> IDataObjectStore

            // Saved and deleted well inside the grace window. The window
            // must NOT apply: these are the bytes the caller just
            // dereferenced, and an erasure's guarantee is that they are
            // gone at rest — Phase 105 asserts exactly this on the KB
            // retention path, which is why the rule is a union rather
            // than a blanket age filter.
            let content = saveComplete storage "obj-1" (bytesOf "payload")

            store.Delete(scope, "obj-1")
            |> Async.RunSynchronously
            |> fun r -> Expect.isOk r "the delete succeeds"

            Expect.isFalse
                (storage.Names container |> List.contains content)
                "the deleted object's own content is gone at rest, not merely dereferenced"
        }

        test "Erase HardDelete removes the subject's bytes inside the window too" {
            let storage = AgeStampedBlobStorage()
            let store = DataObjectStore.DataObjectStore(storage, noopLogger) :> IDataObjectStore

            let content = saveComplete storage "obj-1" (bytesOf "personal")

            store.Erase(scope, "user-1", ErasurePolicy.HardDelete, false)
            |> Async.RunSynchronously
            |> fun r -> Expect.isOk r "the erasure succeeds"

            Expect.isFalse
                (storage.Names container |> List.contains content)
                "the erased subject's bytes are removed, not deferred to a sweep that may not be composed"
        }

        test "the in-band GC still reclaims a genuinely-old unreferenced orphan (GP 11)" {
            let storage = AgeStampedBlobStorage()

            // A crash residue — no operation ever released it, so only
            // the AGE arm of the rule can reclaim it.
            let stranded =
                strandContent storage "obj-crashed" (bytesOf "residue")
                |> Async.RunSynchronously

            storage.Backdate(container, stranded, DateTime.UtcNow - TimeSpan.FromDays 2.0)

            // Any in-band pass in this container should sweep it up.
            let store = DataObjectStore.DataObjectStore(storage, noopLogger) :> IDataObjectStore
            saveComplete storage "obj-other" (bytesOf "other") |> ignore

            store.Delete(scope, "obj-other")
            |> Async.RunSynchronously
            |> fun r -> Expect.isOk r "the unrelated delete succeeds"

            Expect.isFalse
                (storage.Names container |> List.contains stranded)
                "an old orphan is reclaimed inline, exactly as before Phase 634"

            Expect.isEmpty
                (DataObjectStore.listOrphanedContent storage container |> Async.RunSynchronously)
                "nothing is left orphaned"
        }

        test "the in-band window is the sweep's published minimum" {
            let minimum = DataObjectOrphanSweepPolicy.MinimumGracePeriod

            // Strand an UNRELEASED orphan aged `age`, run an unrelated
            // `Delete`, and report whether it survived. Unreleased is
            // load-bearing — a released blob goes regardless of age, so
            // this boundary would not be measurable through one.
            let survivesAtAge (age: TimeSpan) =
                let storage = AgeStampedBlobStorage()

                let stranded =
                    strandContent storage "obj-crashed" (bytesOf "residue")
                    |> Async.RunSynchronously

                storage.Backdate(container, stranded, DateTime.UtcNow - age)

                let store = DataObjectStore.DataObjectStore(storage, noopLogger) :> IDataObjectStore
                saveComplete storage "obj-other" (bytesOf "other") |> ignore
                store.Delete(scope, "obj-other") |> Async.RunSynchronously |> ignore

                storage.Names container |> List.contains stranded

            Expect.isTrue
                (survivesAtAge (minimum - TimeSpan.FromSeconds 30.0))
                "an orphan younger than the sweep's published minimum grace is deferred in-band"

            Expect.isFalse
                (survivesAtAge (minimum + TimeSpan.FromSeconds 30.0))
                "an orphan older than it is reclaimed in-band"
        }

        test "a blob deferred in-band is still reclaimable by the scheduled sweep" {
            let storage = AgeStampedBlobStorage()

            // The residue Phase 634 newly leaves: a fresh unreleased
            // orphan the in-band pass declines. The Phase 7c sweep is
            // what accounts for it — the handoff, asserted end to end.
            let stranded =
                strandContent storage "obj-crashed" (bytesOf "residue")
                |> Async.RunSynchronously

            let store = DataObjectStore.DataObjectStore(storage, noopLogger) :> IDataObjectStore
            saveComplete storage "obj-other" (bytesOf "other") |> ignore
            store.Delete(scope, "obj-other") |> Async.RunSynchronously |> ignore

            Expect.contains (storage.Names container) stranded "in-band deferred it"

            let orphans =
                DataObjectStore.listOrphanedContent storage container |> Async.RunSynchronously

            Expect.hasLength orphans 1 "and it is visible to the sweep as an orphan"

            let now = DateTime.UtcNow
            storage.Backdate(container, stranded, now - TimeSpan.FromHours 25.0)

            let audit = CapturingAuditLog()
            let report = sweep storage (Some(audit :> IAuditLog)) now (TimeSpan.FromHours 24.0)

            Expect.hasLength report.Reclaimed 1 "the scheduled sweep reclaims it once past its own window"
            Expect.equal report.DeferredByGrace 0 "and its accounting is undisturbed"
            Expect.isFalse (storage.Names container |> List.contains stranded) "the blob is gone"
        }

        test "Evict defers a concurrent Save's content on the same path" {
            let storage = AgeStampedBlobStorage()
            let store = DataObjectStore.DataObjectStore(storage, noopLogger) :> IDataObjectStore

            // Evict and Erase share `collectOrphanedContent` with Delete;
            // one of them is pinned here so a future refactor that fixes
            // only the Delete path cannot pass.
            let aContent = saveComplete storage "obj-a" (bytesOf "a-content")
            storage.Backdate(container, aContent, DateTime.UtcNow - TimeSpan.FromHours 1.0)

            let mutable interleaved = false

            storage.OnUploaded <-
                fun name ->
                    if not interleaved && name.Contains "_content/" then
                        interleaved <- true
                        store.Evict(scope, "obj-a") |> Async.RunSynchronously |> ignore

            store.Save(scope, "obj-b", bytesOf "b-content", "text/plain", "user-1", Map.empty, Unversioned)
            |> Async.RunSynchronously
            |> ignore

            storage.OnUploaded <- fun _ -> ()

            Expect.isTrue interleaved "the evict landed inside the gap"

            match store.Get(scope, "obj-b") |> Async.RunSynchronously with
            | Ok(_, bytes) -> Expect.equal bytes (bytesOf "b-content") "B's content survives the racing evict"
            | Error e -> failtestf "B's content was destroyed by the racing in-band GC: %A" e

            Expect.isFalse
                (storage.Names container |> List.contains aContent)
                "A's own out-of-window content was still reclaimed"
        }
    ]

// ─── Preflight validator + compose surface ───────────────────────────

let private validate (config: ServerConfig) (services: IServiceCollection) =
    (DataObjectOrphanSweepConfiguredValidator(config, services) :> ConfigValidation.IConfigValidator).Validate()
    |> Async.RunSynchronously

let private status = ConfigValidation.ValidationResult.status

[<Tests>]
let validatorTests =
    testList "Phase 7c — orphan-sweep preflight validator" [

        test "persistent deployment with no sweep composed warns and names the fix" {
            let config = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.team
            }

            let result = validate config (ServiceCollection())
            Expect.equal (status result) "Warning" "an unswept persistent content pool is flagged"

            let message = ConfigValidation.ValidationResult.message result

            Expect.stringContains message "withDataObjectOrphanSweep" "the warning names the builder call that fixes it"

            Expect.stringContains message "DataObjectOrphanSweepPolicy.disabled" "and the acknowledgement path"
        }

        test "anonymous-only deployment is not warned" {
            let config = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.anonymous
            }

            Expect.equal
                (status (validate config (ServiceCollection())))
                "Ok"
                "an ephemeral anonymous deployment has no long-lived pool to leak into"
        }

        test "composing the inert policy is an acknowledgement and validates Ok" {
            let config = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.team
            }

            let services = ServiceCollection()

            services.AddSingleton<DataObjectOrphanSweepPolicy>(DataObjectOrphanSweepPolicy.disabled)
            |> ignore

            Expect.equal
                (status (validate config services))
                "Ok"
                "composing `disabled` records that the operator accepts the leak"
        }

        test "an active sweep with no job scheduler warns — the job can never fire" {
            let config = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.team
                    JobScheduler = NoJobScheduler
            }

            let services = ServiceCollection()

            services.AddSingleton<DataObjectOrphanSweepPolicy>(DataObjectOrphanSweepPolicy.forScopes [ scope ])
            |> ignore

            let result = validate config services
            Expect.equal (status result) "Warning" "a declared job with no scheduler is a config mismatch"

            Expect.stringContains
                (ConfigValidation.ValidationResult.message result)
                "InProcessJobScheduler"
                "the warning names the scheduler setting"
        }

        test "an active sweep with a scheduler validates Ok" {
            let config = {
                ServerConfig.defaults with
                    Surfaces = Surfaces.team
                    JobScheduler = InProcessJobScheduler
            }

            let services = ServiceCollection()

            services.AddSingleton<DataObjectOrphanSweepPolicy>(DataObjectOrphanSweepPolicy.forScopes [ scope ])
            |> ignore

            Expect.equal (status (validate config services)) "Ok" "composed and schedulable"
        }
    ]

[<Tests>]
let composeTests =
    testList "Phase 7c — withDataObjectOrphanSweep composition" [

        /// GP 11 / GP 13 asserted structurally: what reaches the service
        /// collection, not what a log line says reached it.
        let registered (app: ServerApp) =
            let services = ServiceCollection() :> IServiceCollection

            match app.Extensions.ServiceConfig with
            | Some register -> register services |> ignore
            | None -> ()

            services

        test "an uncomposed app registers nothing at all (GP 13)" {
            let services = registered ServerApp.empty

            Expect.isEmpty
                (services
                 |> Seq.filter (fun d -> d.ServiceType = typeof<DataObjectOrphanSweepPolicy>))
                "no policy singleton"
        }

        test "the inert policy registers the acknowledgement but no hosted service" {
            let services =
                ServerApp.empty
                |> ServerApp.withDataObjectOrphanSweep DataObjectOrphanSweepPolicy.disabled
                |> registered

            Expect.hasLength
                (services
                 |> Seq.filter (fun d -> d.ServiceType = typeof<DataObjectOrphanSweepPolicy>)
                 |> List.ofSeq)
                1
                "the policy is registered so preflight sees the acknowledgement"

            Expect.isEmpty
                (services
                 |> Seq.filter (fun d -> d.ServiceType = typeof<Microsoft.Extensions.Hosting.IHostedService>))
                "an inert policy schedules nothing — no hosted service, no scheduler entry"
        }

        test "a scoped policy registers the deferred-declaration hosted service" {
            let services =
                ServerApp.empty
                |> ServerApp.withDataObjectOrphanSweep (DataObjectOrphanSweepPolicy.forScopes [ scope ])
                |> registered

            Expect.hasLength
                (services
                 |> Seq.filter (fun d -> d.ServiceType = typeof<DataObjectOrphanSweepPolicy>)
                 |> List.ofSeq)
                1
                "the policy is registered"

            Expect.hasLength
                (services
                 |> Seq.filter (fun d -> d.ServiceType = typeof<Microsoft.Extensions.Hosting.IHostedService>)
                 |> List.ofSeq)
                1
                "and one hosted service registers + schedules the job at StartAsync"
        }
    ]
module ToolUp.Platform.Tests.InProcess.BlobPendingInviteStoreTests

open System
open System.Text
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Scope.Teams
open ToolUp.Platform.Scope.Teams.PendingInviteStoreInstanceValidator
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Config

// ─── Phase 5h — BlobPendingInviteStore contract binding + concurrency ─
//
// Runs the `IPendingInviteStoreContract` pack against the ETag-based
// multi-instance default, then adds what the pack deliberately leaves
// implementation-specific: the concurrency story (two replicas both
// land; two replicas consuming one entry admit exactly one), the
// bounded retry (N conflicts then success; N+1 conflicts surface
// `Conflict`), the absent-blob create-if-absent path, the corrupt-blob
// fail-closed path, the `TryCreate` probe, and the
// `PendingInviteStoreInstanceValidator` gate over the resolved store.
//
// The Blob store holds NO process-level state, so — unlike the InMemory
// binding — nothing here needs `testSequenced`: every factory call gets
// its own `InMemoryBlobStorage` (which implements the Phase 600
// `IConditionalBlobStorage` seam with a content-hash etag and a
// serialised CAS), and "two replicas" is simply two store instances over
// one storage. The one test that also constructs an InMemory store (the
// migration-compatibility check) resets that store's process-wide cache
// first, as its own binding does.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// A budget that keeps the forced-conflict tests fast: the backoff is
/// still exercised (the loop calls `Async.Sleep` for any positive pause)
/// but at a millisecond rather than the shard's 100 ms.
let private fastOptions (maxRetries: int) : BlobPendingInviteStoreOptions = {
    MaxRetries = maxRetries
    InitialBackoff = TimeSpan.FromMilliseconds 1.0
}

let private freshPending teamId : PendingInviteByEmail = {
    TeamId = teamId
    Role = Member
    ExpiresAt = DateTime.UtcNow.AddDays 7.0
    InviterUserId = "alice@example.com"
    IssuedAt = DateTime.UtcNow
}

/// Decorates a conditional storage so the first `refuseFirst` conditional
/// uploads are refused with `ETagMismatch` and every later one delegates.
/// The refusals are counted across ALL uploads, which is exactly what a
/// forced N-conflict test wants: the store's loop re-reads and retries,
/// and the (N+1)th attempt reaches the real backend.
/// Both seams over one in-memory backend, with the conditional upload of
/// DOCUMENTS replaceable. The document store's capability probe
/// (`*.etag-probe`) always reaches the backend, so an injected refusal or
/// failure is spent on a document write, never on the probe.
type private CasDouble
    (
        inner: InMemoryBlobStorage,
        upload: string * string * byte[] * ETagCondition -> Async<Result<string, ConditionalWriteError>>
    ) =
    let plain = inner :> IBlobStorage
    let cas = inner :> IConditionalBlobStorage

    interface IBlobStorage with
        member _.CanComposeFrom = plain.CanComposeFrom
        member _.ComposeFrom(c, t, s) = plain.ComposeFrom(c, t, s)
        member _.Erase(c, p, policy, dryRun) = plain.Erase(c, p, policy, dryRun)
        member _.Upload(c, b, content) = plain.Upload(c, b, content)
        member _.Download(c, b) = plain.Download(c, b)
        member _.DownloadRange(c, b, o, l) = plain.DownloadRange(c, b, o, l)
        member _.Delete(c, b) = plain.Delete(c, b)
        member _.List(c, p) = plain.List(c, p)
        member _.Exists(c, b) = plain.Exists(c, b)
        member _.GetMetadata(c, b) = plain.GetMetadata(c, b)

    interface IConditionalBlobStorage with
        member _.DownloadWithETag(container, blobName) =
            cas.DownloadWithETag(container, blobName)

        member _.UploadWithETag(container, blobName, content, condition) =
            if blobName.EndsWith ".etag-probe" then
                cas.UploadWithETag(container, blobName, content, condition)
            else
                upload (container, blobName, content, condition)

/// Refuses the first `refuseFirst` document writes as a lost precondition.
let private conflictInjecting (inner: InMemoryBlobStorage) (refuseFirst: int) =
    let mutable attempts = 0
    let cas = inner :> IConditionalBlobStorage

    CasDouble(
        inner,
        fun (container, blobName, content, condition) -> async {
            if Interlocked.Increment &attempts <= refuseFirst then
                // Refused as a real backend refuses: naming the current etag,
                // or none while the blob is still absent.
                let! current = cas.DownloadWithETag(container, blobName)

                return
                    Error(
                        ETagMismatch(
                            match current with
                            | Ok(_, etag) -> Some etag
                            | Error _ -> None
                        )
                    )
            else
                return! cas.UploadWithETag(container, blobName, content, condition)
        }
    )
    :> IBlobStorage

type private PlainBlobStorage() =
    let inner = InMemoryBlobStorage() :> IBlobStorage

    interface IBlobStorage with
        member _.CanComposeFrom = inner.CanComposeFrom
        member _.ComposeFrom(c, t, s) = inner.ComposeFrom(c, t, s)
        member _.Erase(c, p, policy, dryRun) = inner.Erase(c, p, policy, dryRun)
        member _.Upload(c, b, content) = inner.Upload(c, b, content)
        member _.Download(c, b) = inner.Download(c, b)
        member _.DownloadRange(c, b, o, l) = inner.DownloadRange(c, b, o, l)
        member _.Delete(c, b) = inner.Delete(c, b)
        member _.List(c, p) = inner.List(c, p)
        member _.Exists(c, b) = inner.Exists(c, b)
        member _.GetMetadata(c, b) = inner.GetMetadata(c, b)

/// Records every audit row the store emits.
type private CapturingAuditLog() =
    let rows = System.Collections.Concurrent.ConcurrentQueue<string * AuditEvent>()

    member _.Rows = rows |> Seq.toList

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { rows.Enqueue(scopeId, audit) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

let private okOrFail label result =
    match result with
    | Ok v -> v
    | Error err -> failtestf "%s: expected Ok, got %A" label err

let private storeOver (storage: IBlobStorage) =
    BlobPendingInviteStore(storage, silentLogger) :> IPendingInviteStore

let private replicasOver (storage: InMemoryBlobStorage) =
    let blobs = storage :> IBlobStorage
    storeOver blobs, storeOver blobs

let private contractBinding =
    let factory () =
        let storage = InMemoryBlobStorage() :> IBlobStorage
        storeOver storage

    IPendingInviteStoreContract.tests "BlobPendingInviteStore" factory

let private concurrencyTests =
    testList "BlobPendingInviteStore — concurrency" [

        testCaseAsync "two replicas upserting concurrently both land — neither update is lost"
        <| async {
            let storage = InMemoryBlobStorage()
            let a, b = replicasOver storage

            // Both replicas race from the same (absent) blob: the loser of
            // the IfAbsent / IfMatch race re-reads and replays its own
            // mutation over the winner's map.
            let! results =
                Async.Parallel [
                    a.Upsert("one@example.com", freshPending "team-a")
                    b.Upsert("two@example.com", freshPending "team-b")
                ]

            for r in results do
                okOrFail "concurrent Upsert" r

            let! listed = a.ListAll()
            let entries = okOrFail "ListAll" listed |> List.map fst |> List.sort
            Expect.equal entries [ "one@example.com"; "two@example.com" ] "both entries persisted"
        }

        testCaseAsync "a burst of concurrent upserts across replicas all land"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            // Twelve writers on one blob is more contention than the
            // shard's 3-retry budget is sized for (that is what the Conflict
            // test below pins), so the burst runs on a widened budget; what
            // is asserted is that every writer that reported Ok is in the
            // blob and that no successful write erased a peer's.
            let replica () =
                BlobPendingInviteStore(storage, silentLogger, None, None, fastOptions 50) :> IPendingInviteStore

            let a, b = replica (), replica ()

            let! results =
                [ 1..12 ]
                |> List.map (fun i ->
                    let store = if i % 2 = 0 then a else b
                    store.Upsert(sprintf "user%02d@example.com" i, freshPending "team-burst"))
                |> Async.Parallel

            for r in results do
                okOrFail "burst Upsert" r

            let! listed = a.ListAll()
            Expect.equal (okOrFail "ListAll" listed).Length 12 "all twelve entries persisted — no lost update"
        }

        testCaseAsync "two replicas consuming the same entry concurrently admit exactly one"
        <| async {
            let storage = InMemoryBlobStorage()
            let a, b = replicasOver storage
            do! a.Upsert("grace@example.com", freshPending "team-consume") |> Async.Ignore

            let! results =
                Async.Parallel [
                    a.TryConsumeForEmail "grace@example.com"
                    b.TryConsumeForEmail "grace@example.com"
                ]

            let admitted =
                results
                |> Array.filter (function
                    | Ok(Some _) -> true
                    | _ -> false)
                |> Array.length

            let refused =
                results
                |> Array.filter (function
                    | Ok None -> true
                    | _ -> false)
                |> Array.length

            Expect.equal admitted 1 "exactly one replica consumed the entry"
            Expect.equal refused 1 "the other saw nothing — no double auto-join"

            let! listed = a.ListAll()
            Expect.equal (okOrFail "ListAll" listed).Length 0 "entry gone after the single consume"
        }

        testCaseAsync "a peer's write between read and write is replayed over, not lost"
        <| async {
            // Deterministic version of the race: replica A's first upload is
            // refused (as if B had written meanwhile); B DOES write in that
            // window; A's retry must then carry both entries.
            let storage = InMemoryBlobStorage()
            let cas = storage :> IConditionalBlobStorage
            let b = storeOver storage

            let injecting =
                CasDouble(
                    storage,
                    fun (c, n, content, condition) -> async {
                        // Simulate the peer landing first, then refuse this
                        // attempt exactly as a real backend would (the etag A
                        // read is now stale).
                        match condition with
                        | IfAbsent ->
                            do! b.Upsert("peer@example.com", freshPending "team-peer") |> Async.Ignore
                            return Error(ETagMismatch(Some "peer-won"))
                        | IfMatch _ -> return! cas.UploadWithETag(c, n, content, condition)
                    }
                )

            let a =
                BlobPendingInviteStore(injecting, silentLogger, None, None, fastOptions 3) :> IPendingInviteStore

            let! result = a.Upsert("mine@example.com", freshPending "team-mine")
            okOrFail "Upsert after replay" result

            let! listed = b.ListAll()
            let entries = okOrFail "ListAll" listed |> List.map fst |> List.sort
            Expect.equal entries [ "mine@example.com"; "peer@example.com" ] "A replayed over B's write"
        }
    ]

let private retryTests =
    testList "BlobPendingInviteStore — bounded retry" [

        testCaseAsync "N forced conflicts then success — the (N+1)th attempt lands"
        <| async {
            let injecting = conflictInjecting (InMemoryBlobStorage()) 3

            let store =
                BlobPendingInviteStore(injecting, silentLogger, None, None, fastOptions 3) :> IPendingInviteStore

            let! result = store.Upsert("retry@example.com", freshPending "team-retry")
            okOrFail "Upsert within budget" result

            let! listed = store.ListAll()
            Expect.equal (okOrFail "ListAll" listed).Length 1 "entry landed on the fourth attempt"
        }

        testCaseAsync "N+1 forced conflicts surface Conflict and write nothing"
        <| async {
            let injecting = conflictInjecting (InMemoryBlobStorage()) 4

            let store =
                BlobPendingInviteStore(injecting, silentLogger, None, None, fastOptions 3) :> IPendingInviteStore

            let! result = store.Upsert("hot@example.com", freshPending "team-hot")

            match result with
            | Error PendingInviteStoreError.Conflict -> ()
            | other -> failtestf "expected Error Conflict after the budget, got %A" other

            let! listed = store.ListAll()
            Expect.equal (okOrFail "ListAll" listed).Length 0 "nothing was written"
        }

        testCaseAsync "MaxRetries = 0 is a single attempt"
        <| async {
            let injecting = conflictInjecting (InMemoryBlobStorage()) 1

            let store =
                BlobPendingInviteStore(injecting, silentLogger, None, None, fastOptions 0) :> IPendingInviteStore

            let! result = store.Remove "nobody@example.com"

            // Remove of an absent key never writes, so it cannot conflict —
            // it is NotFound from the read alone.
            match result with
            | Error PendingInviteStoreError.NotFound -> ()
            | other -> failtestf "expected NotFound, got %A" other

            let! result = store.Upsert("once@example.com", freshPending "team-once")

            match result with
            | Error PendingInviteStoreError.Conflict -> ()
            | other -> failtestf "expected Conflict on the single refused attempt, got %A" other
        }

        testCaseAsync "an infrastructure failure is StorageFailed, not retried as a conflict"
        <| async {
            let mutable uploads = 0

            let failing =
                CasDouble(
                    InMemoryBlobStorage(),
                    fun _ -> async {
                        uploads <- uploads + 1
                        return Error(ConditionalWriteFailure "backend 503")
                    }
                )

            let store =
                BlobPendingInviteStore(failing, silentLogger, None, None, fastOptions 3) :> IPendingInviteStore

            let! result = store.Upsert("down@example.com", freshPending "team-down")

            match result with
            | Error(PendingInviteStoreError.StorageFailed msg) ->
                Expect.stringContains msg "backend 503" "the backend's message is carried"
            | other -> failtestf "expected StorageFailed, got %A" other

            Expect.equal uploads 1 "one attempt — a failing backend is not looped against"
        }
    ]

let private substrateTests =
    testList "BlobPendingInviteStore — substrate" [

        testCaseAsync "absent blob: the first write is create-if-absent and lands"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let store = storeOver storage

            let! before = storage.Exists("_platform", "pending-invites.json")
            Expect.isFalse before "blob absent before the first write"

            do! store.Upsert("first@example.com", freshPending "team-first") |> Async.Ignore

            let! after = storage.Exists("_platform", "pending-invites.json")
            Expect.isTrue after "the write created the blob"
        }

        testCaseAsync "same blob + codec as InMemoryPendingInviteStore — entries survive a store swap both ways"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let blob = storeOver storage

            do!
                blob.Upsert("blob-written@example.com", freshPending "team-swap")
                |> Async.Ignore

            // The single-instance store reads what the blob store wrote…
            let inMemory =
                InMemoryPendingInviteStore(storage, silentLogger) :> IPendingInviteStore

            let! seen = inMemory.ListAll()

            Expect.equal
                (okOrFail "InMemory ListAll" seen |> List.map fst)
                [ "blob-written@example.com" ]
                "InMemory reads the Blob store's entry"

            // …and vice versa (the same document, written without a
            // precondition).
            do!
                inMemory.Upsert("inmemory-written@example.com", freshPending "team-swap")
                |> Async.Ignore

            let! back = blob.ListAll()

            Expect.equal
                (okOrFail "Blob ListAll" back |> List.map fst |> List.sort)
                [ "blob-written@example.com"; "inmemory-written@example.com" ]
                "Blob reads the InMemory store's entry"
        }

        testCaseAsync "corrupt blob: quarantined and left in place, every operation refused, nothing written"
        <| async {
            // Phase 1061 retired the heal-to-empty: an empty map written over
            // bytes nobody could read loses every invite in them.
            let storage = InMemoryBlobStorage()
            let plain = storage :> IBlobStorage
            let store = storeOver plain

            let! _ = plain.Upload("_platform", "pending-invites.json", Encoding.UTF8.GetBytes "{ not json")

            let! result = store.Upsert("victim@example.com", freshPending "team-corrupt")

            match result with
            | Error(PendingInviteStoreError.StorageFailed msg) ->
                Expect.stringContains msg "copied to" "the failure names the quarantine copy"
            | other -> failtestf "expected StorageFailed on a corrupt blob, got %A" other

            let! names = plain.List("_platform", "pending-invites.json")
            let quarantined = names |> List.filter (fun n -> n.Contains ".corrupt-")
            Expect.equal quarantined.Length 1 "the corrupt bytes were copied aside"

            let! canonical = plain.Download("_platform", "pending-invites.json")

            Expect.equal
                (okOrFail "canonical download" canonical |> Encoding.UTF8.GetString)
                "{ not json"
                "the canonical blob is untouched — not healed to empty"

            let! listed = store.ListAll()

            match listed with
            | Error(PendingInviteStoreError.StorageFailed _) -> ()
            | other -> failtestf "expected ListAll to refuse over the corrupt blob, got %A" other
        }

        testCaseAsync "SweepExpired emits one TeamInviteExpired per dropped entry, after the write"
        <| async {
            let storage = InMemoryBlobStorage()
            let audit = CapturingAuditLog()

            let store =
                BlobPendingInviteStore((storage :> IBlobStorage), silentLogger, Some(audit :> IAuditLog))
                :> IPendingInviteStore

            let near = {
                freshPending "team-audit" with
                    ExpiresAt = DateTime.UtcNow.AddMilliseconds 150.0
            }

            do! store.Upsert("lapsing@example.com", near) |> Async.Ignore
            do! Async.Sleep 350

            let! swept = store.SweepExpired()
            Expect.equal (okOrFail "SweepExpired" swept) 1 "one entry swept"

            let expiredRows =
                audit.Rows
                |> List.choose (fun (scope, evt) ->
                    match evt with
                    | TeamInviteExpired p when scope = "team-audit" -> Some p
                    | _ -> None)

            Expect.equal expiredRows.Length 1 "one TeamInviteExpired row under the team scope"
            Expect.equal expiredRows.Head.InviteeEmail "lapsing@example.com" "row names the invitee"
        }

        testCase "TryCreate probes for the conditional seam"
        <| fun () ->
            let capable =
                BlobPendingInviteStore.TryCreate((InMemoryBlobStorage() :> IBlobStorage), silentLogger)

            Expect.isSome capable "a backend implementing IConditionalBlobStorage yields a store"

            let incapable =
                BlobPendingInviteStore.TryCreate((PlainBlobStorage() :> IBlobStorage), silentLogger)

            Expect.isNone incapable "a plain IBlobStorage yields None — never a racy fallback"

            Expect.throwsT<ArgumentException>
                (fun () ->
                    BlobPendingInviteStore((PlainBlobStorage() :> IBlobStorage), silentLogger)
                    |> ignore)
                "the constructor refuses a backend without conditional writes"
    ]

let private validatorTests =
    let baseConfig = {
        ServerConfig.defaults with
            MultiInstance = {
                ServerConfig.defaults.MultiInstance with
                    ReplicaCount = 3
            }
    }

    let validate (cfg: ServerConfig) (store: IPendingInviteStore option) =
        (PendingInviteStoreInstanceValidator(cfg, store) :> ConfigValidation.IConfigValidator).Validate()
        |> Async.RunSynchronously

    let isWarning =
        function
        | ConfigValidation.Warning _ -> true
        | _ -> false

    let inMemory () =
        InMemoryPendingInviteStore((InMemoryBlobStorage() :> IBlobStorage), silentLogger) :> IPendingInviteStore

    let blob () =
        storeOver (InMemoryBlobStorage() :> IBlobStorage)

    testList "PendingInviteStoreInstanceValidator — Phase 5h gate" [

        test "InMemory store under ReplicaCount > 1 warns" {
            Expect.isTrue (isWarning (validate baseConfig (Some(inMemory ())))) "warning fires on the in-memory store"
        }

        test "Blob store under ReplicaCount > 1 is clean" {
            Expect.equal (validate baseConfig (Some(blob ()))) ConfigValidation.Ok "no warning on the ETag-based store"
        }

        test "a custom store under ReplicaCount > 1 is clean" {
            let custom =
                { new IPendingInviteStore with
                    member _.Upsert(_, _) = async { return Ok() }
                    member _.Remove _ = async { return Ok() }
                    member _.TryConsumeForEmail _ = async { return Ok None }
                    member _.ListAll() = async { return Ok [] }
                    member _.SweepExpired() = async { return Ok 0 }
                }

            Expect.equal
                (validate baseConfig (Some custom))
                ConfigValidation.Ok
                "an unknown store is not assumed in-memory"
        }

        test "InMemory store on a single replica is clean" {
            let single = {
                baseConfig with
                    MultiInstance = {
                        baseConfig.MultiInstance with
                            ReplicaCount = 1
                    }
            }

            Expect.equal
                (validate single (Some(inMemory ())))
                ConfigValidation.Ok
                "single instance is the in-memory store's home"
        }

        test "the escape hatch still silences the in-memory warning" {
            let accepted = {
                baseConfig with
                    RiskAcknowledgements = {
                        baseConfig.RiskAcknowledgements with
                            AcceptPendingInviteStoreInMultiInstance = true
                    }
            }

            Expect.equal (validate accepted (Some(inMemory ()))) ConfigValidation.Ok "explicit opt-in wins"
        }

        test "the pre-5h constructor assumes the in-memory store" {
            let legacy =
                (PendingInviteStoreInstanceValidator(baseConfig) :> ConfigValidation.IConfigValidator).Validate()
                |> Async.RunSynchronously

            Expect.isTrue (isWarning legacy) "no resolved store in hand → the old reading"
        }

        test "the warning names the remedy, not the retired Phase 9c wait" {
            match validate baseConfig (Some(inMemory ())) with
            | ConfigValidation.Warning msg ->
                Expect.stringContains msg "BlobPendingInviteStore" "names the store that clears it"
                Expect.stringContains msg "IConditionalBlobStorage" "names what that store needs"
                Expect.isFalse (msg.Contains "Phase 9c") "no longer tells the operator to wait"
            | other -> failtestf "expected Warning, got %A" other
        }
    ]

let tests =
    testList "BlobPendingInviteStore" [
        contractBinding
        IPendingInviteStoreContract.documentTests
            "BlobPendingInviteStore"
            (fun () -> InMemoryBlobStorage() :> IBlobStorage)
            storeOver
        concurrencyTests
        retryTests
        substrateTests
    ]

let validatorGateTests = validatorTests
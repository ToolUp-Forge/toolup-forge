module ToolUp.Platform.Tests.InProcess.BlobCorruptionChaosTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.Teams
open ToolUp.Platform.Testing
open ToolUp.Platform.Testing.FaultInjectingBlobStorage
open ToolUp.Platform.Tests.Support
open SharedTypes
open KnowledgeBase.ServerIndexStorage

// ─── Phase 205 — blob-corruption chaos / fault-injection pack ────────
//
// Drives the Phase 116 fail-closed blob read-modify-write integrity paths
// under corruption and concurrency, proving they fail **closed** rather
// than degrading to silent-empty or last-writer-wins. Each site is exercised
// through the reusable `FaultInjectingBlobStorage` decorator (Testing) so the
// adversarial conditions — corrupt / truncated / garbage reads, torn /
// dropped writes, a widened read→write window — are injected without touching
// the stores under test.
//
// SCOPE NOTE. Phase 116's ETag-conditional-write half — the generic guarded
// `BlobMapStore` decorator — is **deferred**: it is gated on Phase 9c half-2
// (`IBlobStorage.UploadWithETag`), which has not shipped, so `BlobMapStore.fs`
// does not exist. This pack therefore drives the *shipped* halves of Phase 116:
//   • `InMemoryPendingInviteStore` — fail-closed decode + quarantine of a
//     corrupt full-blob map (never a map derived from a failed decode).
//   • `BlobShareTokenStore.MarkUsed` — the single-instance `claimWriteLock`
//     that keeps a `UseLimit = 1` token to one admission under concurrency,
//     and its fail-closed corrupt-claim read.
//   • KB `IndexStorage.upsertIndexEntry` — the per-container lock that keeps
//     concurrent additive writers from losing index entries.
// The cross-replica CAS properties (a *dropped* write surfaced as an error
// rather than silently lost) are the deferred ETag story; the lost-write case
// below characterises the current single-instance boundary honestly.
//
// DETERMINISM. The decorator is seeded with a fixed value, so byte-corruption
// is reproducible. The concurrency knob (`WidenReadWriteGap`) only makes an
// *unlocked* RMW more likely to lose a write; the assertions never depend on a
// race landing a particular way, so a correctly-locked store cannot flake.

[<Literal>]
let private seed = 20260619

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

// ─── Pending-invites fixtures ────────────────────────────────────────

let private platformContainer = "_platform"
let private pendingInvitesBlob = "pending-invites.json"

let private freshPending teamId : PendingInviteByEmail = {
    TeamId = teamId
    Role = Member
    ExpiresAt = DateTime.UtcNow.AddDays 7.0
    InviterUserId = "alice@example.com"
    IssuedAt = DateTime.UtcNow
}

// ─── Share-token fixtures ────────────────────────────────────────────

let private issueReq (scopeId: string) (resourceId: string) (useLimit: int option option) : ShareTokenIssueRequest = {
    ScopeId = scopeId
    ResourceKind = "forms.publishable"
    ResourceId = resourceId
    AttributedHandle = None
    IssuedBy = "issuer@example.com"
    ExpiresAt = None
    UseLimit = useLimit
    RateLimit = None
}

let private tokenScope () =
    "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)

// ─── KB index fixtures ───────────────────────────────────────────────

let private knowledgeDoc (id: string) : KnowledgeDocument = {
    Id = id
    FileName = id + ".txt"
    FileType = "txt"
    UploadedAt = DateTimeOffset.UtcNow
    UploadedBy = "uploader@example.com"
    Status = Complete 3
    SizeBytes = 100L
    ChunkCount = 3
    Source = UploadedFile
    ContentHash = None
    // Phase 510 — pre-versioning fixture: version 1 of its own lineage.
    Version = 1
    // Phase 502.C — untagged fixture.
    Tags = []
}

// ─── Share-token corrupt-claim cases (download-side faults) ──────────

let private corruptClaimCase (label: string) (fault: BlobFault) =
    testCaseAsync
        $"share-token: {label} claim read → MarkUsed fails closed; sibling token survives"
        (async {
            let inner = Fakes.TestBlobStorage() :> IBlobStorage
            let blob = FaultInjectingBlobStorage(inner, seed)
            let secrets = Fakes.TestSecretStore() :> ISecretStore
            let store = ShareTokenStore.create blob secrets None silentLogger
            let scope = tokenScope ()

            let! xResult = store.Issue(issueReq scope "form-x" (Some(Some 1)))
            let x = Expect.wantOk xResult "issue token X"
            let! yResult = store.Issue(issueReq scope "form-y" (Some(Some 1)))
            let y = Expect.wantOk yResult "issue token Y"

            // Corrupt only X's claim blob at rest; Y is untouched. Claims are
            // one-blob-per-token, so a corrupt claim can only break its own token.
            blob.FaultBlob(platformContainer, $"share-tokens/{scope}/{x.Claim.TokenId}.json", fault)

            let! markX = store.MarkUsed(scope, x.Claim.TokenId)

            match markX with
            | Error(ShareTokenError.StorageFailed _) -> ()
            | other -> failtestf "corrupt claim X should fail closed as StorageFailed, got %A" other

            // The sibling token's blob is intact — it still marks used cleanly.
            let! markY = store.MarkUsed(scope, y.Claim.TokenId)
            Expect.isOk markY "sibling token Y survives corruption of X's claim"
        })

// ─── The pack ────────────────────────────────────────────────────────

let tests =
    testList "BlobCorruptionChaos" [
        // Pending-invites fail-closed decode shares the process-wide
        // PendingInviteStore cache; sequence it (and reset the cache per test)
        // so sibling cache-touching packs cannot interleave on it.
        testSequenced (
            testList "pending-invites fail-closed decode" [
                testCaseAsync
                    "torn write corrupts the map blob; next upsert fails closed + quarantines, never an empty-overwrite"
                    (async {
                        do! CacheReset.invalidateAll ()
                        let inner = Fakes.TestBlobStorage() :> IBlobStorage
                        let blob = FaultInjectingBlobStorage(inner, seed)
                        let store = InMemoryPendingInviteStore(blob, silentLogger) :> IPendingInviteStore

                        // Seed a valid entry with faults disarmed.
                        let! seededA = store.Upsert("a@example.com", freshPending "team-a")
                        Expect.isOk seededA "seed upsert A"

                        // Arm a torn write on the map blob, then upsert B: the store
                        // reads [A] fine and writes [A;B], but only a 5-byte prefix
                        // reaches disk. The store believes the write succeeded.
                        do! CacheReset.invalidateAll ()
                        blob.FaultBlob(platformContainer, pendingInvitesBlob, UploadPartial 5)
                        let! tornB = store.Upsert("b@example.com", freshPending "team-a")
                        Expect.isOk tornB "torn upsert B is Ok from the store's view (write believed to succeed)"

                        // Disarm, drop the cache, force a fresh read of the torn blob.
                        blob.ClearFaults()
                        do! CacheReset.invalidateAll ()
                        let! cUpsert = store.Upsert("c@example.com", freshPending "team-a")

                        match cUpsert with
                        | Error(PendingInviteStoreError.StorageFailed msg) ->
                            Expect.stringContains (msg.ToLowerInvariant()) "corrupt" "error names the corrupt blob"
                        | other -> failtestf "expected StorageFailed on corrupt decode, got %A" other

                        // The corrupt canonical blob was quarantined aside and removed.
                        let! quarantined = inner.List(platformContainer, pendingInvitesBlob + ".corrupt-")
                        Expect.isNonEmpty quarantined "corrupt blob quarantined aside for recovery"
                        let! canonicalExists = inner.Exists(platformContainer, pendingInvitesBlob)
                        Expect.isFalse canonicalExists "canonical blob renamed-aside (self-heals to empty on next read)"

                        // Critically: no map derived from a failed decode was ever
                        // written — C is absent (never an empty-plus-C overwrite).
                        do! CacheReset.invalidateAll ()
                        let! listing = store.ListAll()

                        match listing with
                        | Ok entries ->
                            Expect.isEmpty entries "no empty-plus-one overwrite: C absent, store self-healed to empty"
                        | Error e -> failtestf "ListAll after self-heal should be Ok [], got %A" e
                    })

                testCaseAsync
                    "dropped write preserves prior entries (single-instance lost-write boundary)"
                    (async {
                        do! CacheReset.invalidateAll ()
                        let inner = Fakes.TestBlobStorage() :> IBlobStorage
                        let blob = FaultInjectingBlobStorage(inner, seed)
                        let store = InMemoryPendingInviteStore(blob, silentLogger) :> IPendingInviteStore

                        let! seededA = store.Upsert("a@example.com", freshPending "team-a")
                        Expect.isOk seededA "seed upsert A"

                        // A silently-lost write (the shape an ETag conditional-write
                        // would reject). The store believes B landed...
                        do! CacheReset.invalidateAll ()
                        blob.FaultBlob(platformContainer, pendingInvitesBlob, UploadDrop)
                        let! droppedB = store.Upsert("b@example.com", freshPending "team-a")
                        Expect.isOk droppedB "dropped upsert B is Ok (interim store cannot detect a lost write)"

                        // ...but the durable state is still exactly [A]: the lost write
                        // neither erased A nor corrupted the blob. Detecting the lost B
                        // is the deferred ETag-CAS story (Phase 116).
                        blob.ClearFaults()
                        do! CacheReset.invalidateAll ()
                        let! listing = store.ListAll()

                        match listing with
                        | Ok entries ->
                            let emails = entries |> List.map fst |> Set.ofList
                            Expect.isTrue (emails.Contains "a@example.com") "prior entry A survives a dropped write"

                            Expect.equal
                                entries.Length
                                1
                                "only A is durable; the lost write fabricated and erased nothing"
                        | Error e -> failtestf "ListAll should be Ok, got %A" e
                    })
            ]
        )

        testList "share-token single-instance" [
            corruptClaimCase "corrupt" DownloadCorrupt
            corruptClaimCase "truncated" (DownloadTruncate 4)
            corruptClaimCase "garbage" (DownloadGarbage [| 0uy; 1uy; 2uy; 3uy |])

            testCaseAsync
                "N concurrent MarkUsed against UseLimit = 1 admits exactly one; losers are UseLimitExceeded"
                (async {
                    let inner = Fakes.TestBlobStorage() :> IBlobStorage
                    let blob = FaultInjectingBlobStorage(inner, seed)
                    // Widen the read→write window so an *unlocked* RMW would
                    // double-admit; the claimWriteLock must still hold the invariant.
                    blob.WidenReadWriteGap 15
                    let secrets = Fakes.TestSecretStore() :> ISecretStore
                    let store = ShareTokenStore.create blob secrets None silentLogger
                    let scope = tokenScope ()

                    let! issued = store.Issue(issueReq scope "form-1" (Some(Some 1)))
                    let token = Expect.wantOk issued "issue single-use token"

                    let n = 10

                    let! outcomes =
                        Array.init n (fun _ -> store.MarkUsed(scope, token.Claim.TokenId))
                        |> Async.Parallel

                    let accepted =
                        outcomes
                        |> Array.filter (function
                            | Ok() -> true
                            | _ -> false)
                        |> Array.length

                    Expect.equal accepted 1 "exactly one of N concurrent MarkUsed is admitted"

                    let losers =
                        outcomes
                        |> Array.filter (function
                            | Ok() -> false
                            | _ -> true)

                    Expect.equal losers.Length (n - 1) "every other submission is surfaced as an error"

                    let allUseLimit =
                        losers
                        |> Array.forall (function
                            | Error ShareTokenError.UseLimitExceeded -> true
                            | _ -> false)

                    Expect.isTrue
                        allUseLimit
                        "losers fail with UseLimitExceeded (single-instance use-count invariant holds)"
                })
        ]

        testList "kb-index concurrency" [
            testCaseAsync
                "N concurrent upserts to one container all appear (no lost entries, no orphaned index)"
                (async {
                    let inner = Fakes.TestBlobStorage() :> IBlobStorage
                    let blob = FaultInjectingBlobStorage(inner, seed)
                    // Widen the load→save window so an unlocked additive writer
                    // would drop an entry; the per-container lock must lose none.
                    blob.WidenReadWriteGap 15
                    let container = "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)

                    let n = 8
                    let docs = [ for i in 1..n -> knowledgeDoc $"doc-{i}" ]

                    do!
                        docs
                        |> List.map (fun d -> upsertIndexEntry blob container d)
                        |> Async.Parallel
                        |> Async.Ignore

                    let! index = loadIndex blob container
                    let ids = index |> List.map _.Id |> Set.ofList
                    let expected = docs |> List.map _.Id |> Set.ofList

                    Expect.equal
                        ids
                        expected
                        "every concurrent upsert appears in the index; the container lock loses none"
                })
        ]
    ]
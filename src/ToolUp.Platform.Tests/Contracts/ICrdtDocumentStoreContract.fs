module ToolUp.Platform.Tests.Contracts.ICrdtDocumentStoreContract

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 535 — ICrdtDocumentStore conformance ──────────────────────
//
// Proves the co-editing log contract against the single-instance
// in-memory default plus its fan-out decorator: appends carry a
// per-document monotonic sequence; the opaque cursor honours its three
// laws (empty means "everything", a just-issued cursor means "nothing",
// an unrecognised cursor degrades to "everything" rather than failing);
// compaction replaces the covered prefix with a client-attested merged
// base without re-sending it to a client already past it; documents are
// scope-isolated (GP 4); and the **convergence property** — any
// permutation of the same update set yields the same state vector and
// delivers the same payload set to a joiner.
//
// Payloads are ASCII here purely so a failure is readable. The store
// never interprets them and no test asserts anything about their
// content, which is the substantive claim: swap Yjs for any other
// update-encoding CRDT and this bar is unchanged.
//
// Any external implementation can run the same bar — the store factory
// passed to `contractCases` below is the only thing that would change.
//
// ── Phase 756 — the same bar, bound three ways ──
//
// The fifteen cases are now a FUNCTION of the store under test, and the
// durable blob-backed implementation is held to every one of them
// unchanged. That is the substantive claim of this phase: the seam's
// contract did not move to accommodate a second implementation, so a
// deployment swapping the in-memory log for the blob-backed one inherits
// exactly the behaviour it already relied on.
//
// Three bindings, and the third is not redundant:
//
//   1. in-memory (Phase 535's single-instance default),
//   2. blob-backed with the shipped fold threshold — high enough that no
//      case reaches it, so this arm exercises the loose-log read path,
//   3. blob-backed folding every third update — low enough that most
//      cases cross it repeatedly, so the SAME fifteen assertions run
//      against a store whose log is a mixture of one snapshot blob and a
//      loose tail. Folding is the one thing the blob store does that the
//      seam knows nothing about; running the contract either side of it
//      is what makes "the fold changes no observable behaviour" a
//      tested claim rather than a design intention.
//
// Beyond the shared bar, `blobSpecificCases` covers what only a durable
// store CAN be asked: restart survival, that compaction actually reduces
// stored bytes, that folding reduces blob count without changing what is
// delivered, and that a path-shaped `DocId` cannot escape its own prefix.

let private jsonOptions = FableConverters.create ()

type private RecordingChannel() =
    let published = ConcurrentQueue<string * Notification>()

    member _.Events =
        published
        |> Seq.choose (fun (scope, n) ->
            match n with
            | CustomNotification(key, json) when key = CrdtTopics.Update ->
                Some(scope, JsonSerializer.Deserialize<CrdtUpdateEvent>(json, jsonOptions))
            | _ -> None)
        |> List.ofSeq

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { published.Enqueue(scopeId, notification) }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_) = async { return () }

/// Phase 535's in-memory default, relay-wrapped exactly as `compose`
/// wires it.
let private inMemoryStore (channel: INotificationChannel) : ICrdtDocumentStore =
    NotifyingCrdtDocumentStore(InMemoryCrdtDocumentStore(), channel) :> ICrdtDocumentStore

/// Phase 756's durable store over a hermetic in-memory `IBlobStorage`,
/// wrapped in the SAME relay — which is the point: `compose` differs
/// between the two arms only in which log it hands the decorator.
///
/// A fresh backing store per call, so cases cannot leak state into one
/// another through the blob layer.
let private blobStoreWith (policy: CrdtSnapshotPolicy) (channel: INotificationChannel) : ICrdtDocumentStore =
    NotifyingCrdtDocumentStore(BlobCrdtDocumentStore(InMemoryBlobStorage(), policy), channel) :> ICrdtDocumentStore

let private docA: CrdtDocRef = { Scope = "team-a"; DocId = "doc-1" }
let private docB: CrdtDocRef = { Scope = "team-b"; DocId = "doc-1" }

let private bytes (s: string) = Encoding.UTF8.GetBytes s
let private text (b: byte[]) = Encoding.UTF8.GetString b

let private payloadSet (updates: CrdtUpdate list) =
    updates |> List.map (_.Payload >> text) |> List.sort

/// The conformance bar, as a function of the store under test.
let private contractCases (label: string) (storeUnderTest: INotificationChannel -> ICrdtDocumentStore) =
    testList label [
        testCaseAsync "append assigns a monotonic per-document sequence and echoes the origin session"
        <| async {
            let store = storeUnderTest (RecordingChannel())
            let! first = store.Append(docA, bytes "u1", "session-1")
            let! second = store.Append(docA, bytes "u2", "session-2")

            Expect.isLessThan first.Sequence second.Sequence "sequence is monotonic within one document"
            Expect.equal first.OriginSession "session-1" "origin session recorded verbatim"
            Expect.equal (text second.Payload) "u2" "payload round-trips as opaque bytes"
            Expect.equal second.Ref docA "the update carries its own document ref"
        }

        testCaseAsync "sequences are per-document and carry no cross-document meaning (GP 12 rule 5)"
        <| async {
            let store = storeUnderTest (RecordingChannel())
            let! a1 = store.Append(docA, bytes "a1", "s")
            let! b1 = store.Append(docB, bytes "b1", "s")

            // Two documents, each starting its own count — the shard key
            // is the ref, so the two sequences are incomparable rather
            // than interleaved on a shared counter.
            Expect.equal a1.Sequence b1.Sequence "each document numbers its own log from the same floor"
        }

        testCaseAsync "an append fans out on the document's own scope, never another team's (GP 4)"
        <| async {
            let ch = RecordingChannel()
            let store = storeUnderTest ch
            do! store.Append(docA, bytes "u1", "session-1") |> Async.Ignore
            do! store.Append(docB, bytes "u2", "session-9") |> Async.Ignore

            let scopes = ch.Events |> List.map fst |> List.sort
            Expect.equal scopes [ "team-a"; "team-b" ] "each event published on its own document's scope"

            let changes = ch.Events |> List.map (fun (_, e) -> e.Change)
            Expect.allEqual changes CrdtChange.Appended "an append announces Appended"

            let teamA = ch.Events |> List.filter (fun (s, _) -> s = "team-a")

            Expect.equal
                (teamA |> List.map (fun (_, e) -> text e.Update.Payload))
                [ "u1" ]
                "team-a saw only its own update"
        }

        testCaseAsync "StateVector.empty means 'send everything' (cursor law 1)"
        <| async {
            let store = storeUnderTest (RecordingChannel())
            do! store.Append(docA, bytes "u1", "s") |> Async.Ignore
            do! store.Append(docA, bytes "u2", "s") |> Async.Ignore

            let! all = store.GetDiff(docA, StateVector.empty)
            Expect.equal (payloadSet all) [ "u1"; "u2" ] "the whole document"
        }

        testCaseAsync "a diff against the vector the store just issued is empty (cursor law 2)"
        <| async {
            let store = storeUnderTest (RecordingChannel())
            do! store.Append(docA, bytes "u1", "s") |> Async.Ignore

            let! vector = store.GetStateVector docA
            let! diff = store.GetDiff(docA, vector)
            Expect.isEmpty diff "nothing outstanding"
        }

        testCaseAsync "a client offline for N updates catches up on exactly the tail it missed"
        <| async {
            let store = storeUnderTest (RecordingChannel())
            do! store.Append(docA, bytes "u1", "s1") |> Async.Ignore
            do! store.Append(docA, bytes "u2", "s1") |> Async.Ignore

            // The offline client's retained cursor.
            let! cursor = store.GetStateVector docA

            // …three updates land while it is away.
            do! store.Append(docA, bytes "u3", "s2") |> Async.Ignore
            do! store.Append(docA, bytes "u4", "s2") |> Async.Ignore
            do! store.Append(docA, bytes "u5", "s3") |> Async.Ignore

            let! caughtUp = store.GetDiff(docA, cursor)
            Expect.equal (payloadSet caughtUp) [ "u3"; "u4"; "u5" ] "exactly the missed tail, not the whole history"

            // …and the resumed cursor is then complete.
            let! resumed = store.GetStateVector docA
            let! nothingLeft = store.GetDiff(docA, resumed)
            Expect.isEmpty nothingLeft "converged after catch-up"
        }

        testCaseAsync "an unrecognised cursor degrades to the whole document rather than failing (cursor law 3)"
        <| async {
            let store = storeUnderTest (RecordingChannel())
            do! store.Append(docA, bytes "u1", "s") |> Async.Ignore
            do! store.Append(docA, bytes "u2", "s") |> Async.Ignore

            // A cursor no implementation of this seam issued — e.g. one
            // persisted by a client against a different backing store.
            let foreign = StateVector.ofBytes (bytes "not-a-cursor-this-store-issued")
            let! diff = store.GetDiff(docA, foreign)

            Expect.equal
                (payloadSet diff)
                [ "u1"; "u2" ]
                "re-sending a held update is free; losing one is not — so the safe direction is 'send more'"
        }

        testCaseAsync "an empty document reads as empty rather than failing"
        <| async {
            let store = storeUnderTest (RecordingChannel())
            let! vector = store.GetStateVector docA
            Expect.isTrue (StateVector.isEmpty vector) "an untouched document's cursor is the empty one"

            let! snapshot = store.Snapshot docA
            Expect.isEmpty snapshot.Updates "no content"
            Expect.equal snapshot.Ref docA "the snapshot names its document"
        }

        testCaseAsync "Snapshot is the whole document plus the vector covering it"
        <| async {
            let store = storeUnderTest (RecordingChannel())
            do! store.Append(docA, bytes "u1", "s") |> Async.Ignore
            do! store.Append(docA, bytes "u2", "s") |> Async.Ignore

            let! snapshot = store.Snapshot docA
            Expect.equal (payloadSet snapshot.Updates) [ "u1"; "u2" ] "every payload a joiner needs"

            let! outstanding = store.GetDiff(docA, snapshot.Vector)
            Expect.isEmpty outstanding "the attached vector covers exactly what the snapshot carried"
        }

        testCaseAsync "compaction replaces the covered prefix with the merged base and announces it"
        <| async {
            let ch = RecordingChannel()
            let store = storeUnderTest ch
            do! store.Append(docA, bytes "u1", "s1") |> Async.Ignore
            do! store.Append(docA, bytes "u2", "s1") |> Async.Ignore

            let! covers = store.GetStateVector docA
            do! store.Append(docA, bytes "u3", "s2") |> Async.Ignore

            // The merged base is client-attested — the store cannot
            // compute it, so a participant that holds the document does.
            let! compacted = store.Compact(docA, bytes "merged(u1,u2)", covers)

            Expect.equal
                (payloadSet compacted.Updates)
                [ "merged(u1,u2)"; "u3" ]
                "the covered prefix is gone, the uncovered tail survives"

            let baseUpdate =
                compacted.Updates
                |> List.find (fun u -> u.OriginSession = CrdtDocument.CompactionOrigin)

            Expect.equal (text baseUpdate.Payload) "merged(u1,u2)" "the base is stored under the reserved origin"

            let compactions =
                ch.Events |> List.filter (fun (_, e) -> e.Change = CrdtChange.Compacted)

            Expect.equal (compactions |> List.map fst) [ "team-a" ] "Compacted announced on the document's own scope"
        }

        testCaseAsync "a joiner after compaction reconstructs the document; a client past it is not re-sent the base"
        <| async {
            let store = storeUnderTest (RecordingChannel())
            do! store.Append(docA, bytes "u1", "s1") |> Async.Ignore
            do! store.Append(docA, bytes "u2", "s1") |> Async.Ignore
            let! covers = store.GetStateVector docA
            do! store.Append(docA, bytes "u3", "s2") |> Async.Ignore

            // The cursor a live co-editor holds — already past everything.
            let! liveCursor = store.GetStateVector docA
            do! store.Compact(docA, bytes "merged(u1,u2)", covers) |> Async.Ignore

            let! joiner = store.GetDiff(docA, StateVector.empty)

            Expect.equal
                (payloadSet joiner)
                [ "merged(u1,u2)"; "u3" ]
                "a cold joiner gets the base plus the uncovered tail"

            let! liveDiff = store.GetDiff(docA, liveCursor)

            Expect.isEmpty liveDiff "a client already past the compaction point is not re-sent a base it does not need"
        }

        testCaseAsync "compaction against the empty vector is refused rather than duplicating the document"
        <| async {
            let store = storeUnderTest (RecordingChannel())
            do! store.Append(docA, bytes "u1", "s1") |> Async.Ignore

            let! refused = async {
                try
                    do! store.Compact(docA, bytes "merged", StateVector.empty) |> Async.Ignore
                    return false
                with :? ArgumentException ->
                    return true
            }

            Expect.isTrue refused "StateVector.empty covers nothing — compacting to it would append a second copy"

            let! intact = store.GetDiff(docA, StateVector.empty)
            Expect.equal (payloadSet intact) [ "u1" ] "the refused call left the log untouched"
        }

        testCaseAsync "documents are scope-isolated — the same DocId in two scopes is two documents (GP 4)"
        <| async {
            let store = storeUnderTest (RecordingChannel())
            do! store.Append(docA, bytes "team-a-secret", "s1") |> Async.Ignore
            do! store.Append(docB, bytes "team-b-secret", "s9") |> Async.Ignore

            let! fromA = store.GetDiff(docA, StateVector.empty)
            let! fromB = store.GetDiff(docB, StateVector.empty)

            Expect.equal (payloadSet fromA) [ "team-a-secret" ] "team-a sees only its own document"
            Expect.equal (payloadSet fromB) [ "team-b-secret" ] "team-b sees only its own document"

            // A cross-tenant read is an impossible lookup, not a filtered
            // one: the scope is part of the key.
            let! unknownScope = store.GetDiff({ docA with Scope = "team-c" }, StateVector.empty)
            Expect.isEmpty unknownScope "a scope with no document reads empty"
        }

        testCaseAsync "convergence — any permutation of the same update set yields the same state vector"
        <| async {
            let payloads = [ "u1"; "u2"; "u3"; "u4" ]

            let appendAll (order: string list) = async {
                let store = storeUnderTest (RecordingChannel())

                for p in order do
                    do! store.Append(docA, bytes p, "s-" + p) |> Async.Ignore

                let! vector = store.GetStateVector docA
                let! delivered = store.GetDiff(docA, StateVector.empty)
                return vector, payloadSet delivered
            }

            let! forwardVector, forwardPayloads = appendAll payloads
            let! reverseVector, reversePayloads = appendAll (List.rev payloads)
            let! shuffledVector, shuffledPayloads = appendAll [ "u3"; "u1"; "u4"; "u2" ]

            Expect.equal reverseVector.Bytes forwardVector.Bytes "reversed order converges to the same cursor"
            Expect.equal shuffledVector.Bytes forwardVector.Bytes "shuffled order converges to the same cursor"

            // The substantive half: whatever the interleaving, a joiner
            // is delivered the same set of payloads — none lost, none
            // invented. Order is deliberately NOT asserted; the payloads
            // are commutative, which is the whole premise.
            Expect.equal reversePayloads forwardPayloads "reversed order delivers the same payload set"
            Expect.equal shuffledPayloads forwardPayloads "shuffled order delivers the same payload set"
        }

        testCaseAsync "convergence holds across concurrent appends from several sessions"
        <| async {
            let store = storeUnderTest (RecordingChannel())

            do!
                [
                    for i in 1..24 ->
                        store.Append(docA, bytes (sprintf "u%02d" i), sprintf "s%d" (i % 4))
                        |> Async.Ignore
                ]
                |> Async.Parallel
                |> Async.Ignore

            let! delivered = store.GetDiff(docA, StateVector.empty)

            Expect.equal
                (payloadSet delivered)
                ([ for i in 1..24 -> sprintf "u%02d" i ] |> List.sort)
                "every concurrently-appended update is delivered exactly once"

            let sequences = delivered |> List.map _.Sequence |> List.distinct
            Expect.equal (List.length sequences) 24 "concurrent appends each received a distinct sequence"
        }
    ]

// ─── Phase 756 — what only a DURABLE store can be asked ──────────────
//
// The fifteen cases above are the seam's contract and say nothing about
// storage, which is correct: they must pass for an implementation that
// keeps the log anywhere at all. These cases are the other half —
// claims that are meaningless against an in-memory log and therefore
// could not be part of the shared bar:
//
//   * a document outliving the process that wrote it, and a cursor
//     retained across that boundary still resolving,
//   * `Compact` actually reclaiming bytes rather than merely renaming
//     them,
//   * the snapshot fold being invisible to every read,
//   * an opaque, path-shaped `DocId` staying inside its own prefix,
//   * and one implementation's cursor reading as foreign to another's,
//     which is cursor law 3 across a boundary the shared pack cannot
//     reach on its own.

/// The reserved platform container the durable store writes into. Named
/// here rather than imported: the blob layout is deliberately internal
/// to the implementation, and a test reaching into it would be pinning a
/// contract the seam does not make. What these cases legitimately
/// observe is the store's total footprint, not its filenames.
[<Literal>]
let private PlatformContainer = "_platform"

let private durableStore (blob: IBlobStorage) (policy: CrdtSnapshotPolicy) (channel: INotificationChannel) =
    NotifyingCrdtDocumentStore(BlobCrdtDocumentStore(blob, policy), channel) :> ICrdtDocumentStore

/// Blob count and total stored bytes. The backing store is hermetic and
/// per-case, so this is the document's whole footprint.
let private footprint (blob: IBlobStorage) = async {
    let! names = blob.List(PlatformContainer, "")

    let! sizes =
        names
        |> List.map (fun name -> async {
            let! metadata = blob.GetMetadata(PlatformContainer, name)

            return
                match metadata with
                | Ok m -> m.Size
                | Error _ -> 0L
        })
        |> Async.Parallel

    return List.length names, Array.sum sizes
}

let private blobSpecificCases =
    testList "blob-backed durability (Phase 756)" [
        testCaseAsync "a document written by one store instance is read whole by the next (restart survival)"
        <| async {
            let blob = InMemoryBlobStorage() :> IBlobStorage
            let before = durableStore blob CrdtSnapshotPolicy.defaults (RecordingChannel())
            do! before.Append(docA, bytes "u1", "s1") |> Async.Ignore
            do! before.Append(docA, bytes "u2", "s1") |> Async.Ignore

            // The cursor a live co-editor retains before the process dies.
            let! retained = before.GetStateVector docA
            do! before.Append(docA, bytes "u3", "s2") |> Async.Ignore

            // The restart: a new store, a new relay, the same storage.
            // Nothing at all is carried over in memory.
            let after = durableStore blob CrdtSnapshotPolicy.defaults (RecordingChannel())

            let! whole = after.GetDiff(docA, StateVector.empty)
            Expect.equal (payloadSet whole) [ "u1"; "u2"; "u3" ] "the whole document survived the process that wrote it"

            let! caughtUp = after.GetDiff(docA, retained)

            Expect.equal
                (payloadSet caughtUp)
                [ "u3" ]
                "a cursor retained before the restart still resolves to exactly the missed tail"

            let! resumed = after.Append(docA, bytes "u4", "s3")

            Expect.equal
                resumed.Sequence
                4L
                "the sequence resumed from storage rather than restarting and issuing a sequence twice"
        }

        testCaseAsync "compaction reduces stored bytes without changing any client's converged state"
        <| async {
            // A property over four compaction points in one twelve-update
            // log: whatever prefix is folded away, the store holds
            // strictly fewer bytes and a cold joiner still reconstructs
            // the same document.
            //
            // The merged base is deliberately short, which is also the
            // realistic case — a CRDT's whole-state encoding is normally
            // far smaller than the sum of the updates that produced it.
            // The store's own claim is the pruning; how compact the base
            // is remains the client's business.
            let payloads = [ for i in 1..12 -> sprintf "update-payload-%02d" i ]

            for compactAfter in [ 2; 5; 8; 11 ] do
                let blob = InMemoryBlobStorage() :> IBlobStorage
                let store = durableStore blob CrdtSnapshotPolicy.defaults (RecordingChannel())

                for p in payloads |> List.take compactAfter do
                    do! store.Append(docA, bytes p, "s1") |> Async.Ignore

                let! covers = store.GetStateVector docA
                let tail = payloads |> List.skip compactAfter

                for p in tail do
                    do! store.Append(docA, bytes p, "s2") |> Async.Ignore

                let! blobsBefore, bytesBefore = footprint blob
                let merged = sprintf "merged<=%d" compactAfter
                do! store.Compact(docA, bytes merged, covers) |> Async.Ignore
                let! blobsAfter, bytesAfter = footprint blob

                Expect.isLessThan
                    bytesAfter
                    bytesBefore
                    (sprintf "compacting a %d-update prefix reclaimed stored bytes" compactAfter)

                Expect.isLessThan
                    blobsAfter
                    blobsBefore
                    (sprintf "…and the superseded update blobs were pruned, not merely rewritten (%d)" compactAfter)

                let! joiner = store.GetDiff(docA, StateVector.empty)

                Expect.equal
                    (payloadSet joiner)
                    ((merged :: tail) |> List.sort)
                    "a cold joiner reconstructs the merged base plus the uncovered tail, whatever the compaction point"
        }

        testCaseAsync "a compacted document is still compacted after a restart"
        <| async {
            let blob = InMemoryBlobStorage() :> IBlobStorage
            let before = durableStore blob CrdtSnapshotPolicy.defaults (RecordingChannel())
            do! before.Append(docA, bytes "u1", "s1") |> Async.Ignore
            do! before.Append(docA, bytes "u2", "s1") |> Async.Ignore
            let! covers = before.GetStateVector docA
            do! before.Append(docA, bytes "u3", "s2") |> Async.Ignore
            do! before.Compact(docA, bytes "merged(u1,u2)", covers) |> Async.Ignore

            let after = durableStore blob CrdtSnapshotPolicy.defaults (RecordingChannel())
            let! joiner = after.GetDiff(docA, StateVector.empty)

            Expect.equal
                (payloadSet joiner)
                [ "merged(u1,u2)"; "u3" ]
                "the compacted form is what survives, not the prefix it replaced"

            let! resumed = after.Append(docA, bytes "u4", "s3")

            Expect.equal
                resumed.Sequence
                4L
                "compaction did not rewind the watermark across the restart, so no sequence is issued twice"
        }

        testCaseAsync "folding the log into a snapshot changes the blob count, never the delivered content"
        <| async {
            // Two stores differing in exactly one thing: whether they
            // fold. Folding is below the seam, so every read must agree.
            let loose = InMemoryBlobStorage() :> IBlobStorage
            let folded = InMemoryBlobStorage() :> IBlobStorage
            let never = durableStore loose { SnapshotThreshold = 0 } (RecordingChannel())
            let often = durableStore folded { SnapshotThreshold = 4 } (RecordingChannel())

            for i in 1..20 do
                let payload = bytes (sprintf "u%02d" i)
                do! never.Append(docA, payload, "s1") |> Async.Ignore
                do! often.Append(docA, payload, "s1") |> Async.Ignore

            let! looseBlobs, _ = footprint loose
            let! foldedBlobs, _ = footprint folded
            Expect.isLessThan foldedBlobs looseBlobs "folding leaves fewer blobs behind to read on a cold join"

            let! fromLoose = never.GetDiff(docA, StateVector.empty)
            let! fromFolded = often.GetDiff(docA, StateVector.empty)
            Expect.equal (payloadSet fromFolded) (payloadSet fromLoose) "…and delivers exactly the same payloads"

            let! looseVector = never.GetStateVector docA
            let! foldedVector = often.GetStateVector docA
            Expect.equal foldedVector.Bytes looseVector.Bytes "…and issues the same cursor"

            let! nothingLeft = often.GetDiff(docA, foldedVector)
            Expect.isEmpty nothingLeft "…which still covers everything the fold absorbed"
        }

        testCaseAsync "a path-shaped DocId stays inside its own prefix (GP 4)"
        <| async {
            // `DocId` is opaque and module-owned, so it may legitimately
            // look like a path. Interpolated raw into a blob name, the
            // pair below COLLIDES: the nested document's updates would
            // land under the parent document's own listing prefix, so a
            // read of the parent would return them. A within-scope leak,
            // and one that surfaces only on the day some module picks a
            // path-like id. The same collision exists on `Scope`, by the
            // same mechanism and with the same fix.
            //
            // The pair is chosen to collide rather than merely to look
            // path-shaped: an arbitrary nested id (`notes/private`)
            // lands beside the parent rather than inside its log prefix,
            // so it would pass even unescaped and prove nothing.
            let blob = InMemoryBlobStorage() :> IBlobStorage
            let store = durableStore blob CrdtSnapshotPolicy.defaults (RecordingChannel())
            let parent: CrdtDocRef = { Scope = "team-a"; DocId = "notes" }

            let nested: CrdtDocRef = {
                Scope = "team-a"
                DocId = "notes/log"
            }

            do! store.Append(parent, bytes "parent-only", "s1") |> Async.Ignore
            do! store.Append(nested, bytes "nested-only", "s2") |> Async.Ignore

            let! fromParent = store.GetDiff(parent, StateVector.empty)
            let! fromNested = store.GetDiff(nested, StateVector.empty)

            Expect.equal
                (payloadSet fromParent)
                [ "parent-only" ]
                "the shorter id does not swallow its path-shaped sibling"

            Expect.equal (payloadSet fromNested) [ "nested-only" ] "…and the path-shaped id keeps a log of its own"
        }

        testCaseAsync "a cursor issued by a different implementation is foreign here and degrades to the whole document"
        <| async {
            // Cursor law 3 across an implementation boundary, which the
            // shared pack cannot reach: each store issues its own opaque
            // shape, so the other's must read as unrecognised rather than
            // being misread as a watermark of its own.
            let inMemory = inMemoryStore (RecordingChannel())
            do! inMemory.Append(docA, bytes "elsewhere", "s") |> Async.Ignore
            let! foreign = inMemory.GetStateVector docA

            let store = blobStoreWith CrdtSnapshotPolicy.defaults (RecordingChannel())
            do! store.Append(docA, bytes "u1", "s") |> Async.Ignore
            do! store.Append(docA, bytes "u2", "s") |> Async.Ignore

            let! diff = store.GetDiff(docA, foreign)

            Expect.equal
                (payloadSet diff)
                [ "u1"; "u2" ]
                "one store's cursor is not another's — the safe direction is 'send more', never 'send less'"
        }
    ]

let tests =
    testList "ICrdtDocumentStore contract (Phase 535)" [
        contractCases "in-memory single-instance default (Phase 535)" inMemoryStore
        contractCases "blob-backed durable store (Phase 756)" (blobStoreWith CrdtSnapshotPolicy.defaults)
        contractCases
            "blob-backed durable store, folding every third update (Phase 756)"
            (blobStoreWith { SnapshotThreshold = 3 })
        blobSpecificCases
    ]
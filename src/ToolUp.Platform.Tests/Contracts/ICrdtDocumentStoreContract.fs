module ToolUp.Platform.Tests.Contracts.ICrdtDocumentStoreContract

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open Expecto
open ToolUp.Platform
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
// Any external implementation can run the same bar — `storeUnderTest`
// below is the only thing that would change.

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

/// The store under test, relay-wrapped exactly as `compose` wires it.
let private storeUnderTest (channel: INotificationChannel) : ICrdtDocumentStore =
    NotifyingCrdtDocumentStore(InMemoryCrdtDocumentStore(), channel) :> ICrdtDocumentStore

let private docA: CrdtDocRef = { Scope = "team-a"; DocId = "doc-1" }
let private docB: CrdtDocRef = { Scope = "team-b"; DocId = "doc-1" }

let private bytes (s: string) = Encoding.UTF8.GetBytes s
let private text (b: byte[]) = Encoding.UTF8.GetString b

let private payloadSet (updates: CrdtUpdate list) =
    updates |> List.map (_.Payload >> text) |> List.sort

let tests =
    testList "ICrdtDocumentStore contract (Phase 535)" [
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
module ToolUp.Platform.Tests.Contracts.IIngestionAttemptStoreContract

// The conformance pack for `IIngestionAttemptStore`: what every attempt store
// must do whatever its status type and wherever it settles. The laws are read
// off `AttemptTransition` alone, so a store is held to them without the pack
// knowing its status.

open System
open Expecto
open ToolUp.Platform.Infra

/// One store under test. `Track scope documentId` makes the document one the
/// store settles (a knowledge-base store settles into an index entry, which
/// must exist); a store that needs nothing returns at once.
type AttemptStoreHarness = {
    Store: IIngestionAttemptStore
    Track: string -> string -> Async<unit>
}

let private run = Async.RunSynchronously

let private fresh (prefix: string) =
    prefix + "-" + Guid.NewGuid().ToString("N")

let private report scope documentId attemptId chunk : AttemptReport = {
    Scope = scope
    DocumentId = documentId
    AttemptId = attemptId
    OriginatingUser = None
    Report = chunk
}

let private isTerminal (transition: AttemptTransition) =
    match transition with
    | AttemptTransition.Terminal _ -> true
    | _ -> false

/// The laws, over a fresh harness per case.
let tests (label: string) (factory: unit -> AttemptStoreHarness) =
    let begun (attemptId: string) (chunks: int) =
        let harness = factory ()
        let scope = fresh "team"
        let documentId = fresh "doc"
        run (harness.Track scope documentId)
        run (harness.Store.Begin(scope, documentId, attemptId, chunks))
        harness.Store, scope, documentId

    testList label [
        test "a document no attempt was begun for is dropped" {
            let harness = factory ()
            let scope = fresh "team"
            let documentId = fresh "doc"
            run (harness.Track scope documentId)

            let transition =
                run (harness.Store.Advance(report scope documentId (Some "a") (ChunkReport.Indexed "c0")))

            Expect.equal transition AttemptTransition.Dropped "nothing to advance"
        }

        test "chunks step the attempt and the last one settles it, once" {
            let store, scope, documentId = begun "a" 2

            let advance chunk =
                run (store.Advance(report scope documentId (Some "a") chunk))

            Expect.equal (advance (ChunkReport.Indexed "c0")) AttemptTransition.Stepped "one of two"
            Expect.isTrue (isTerminal (advance (ChunkReport.Indexed "c1"))) "two of two settles"

            Expect.equal
                (advance (ChunkReport.Indexed "c1"))
                AttemptTransition.Dropped
                "a settled attempt does not move"
        }

        test "a chunk counted once is never counted again" {
            let store, scope, documentId = begun "a" 2

            let advance chunk =
                run (store.Advance(report scope documentId (Some "a") chunk))

            Expect.equal (advance (ChunkReport.Indexed "c0")) AttemptTransition.Stepped "counted"
            Expect.equal (advance (ChunkReport.Indexed "c0")) AttemptTransition.Dropped "redelivered: dropped"
            Expect.isTrue (isTerminal (advance (ChunkReport.Indexed "c1"))) "the second distinct chunk settles"
        }

        test "a failure settles the attempt and a later success moves nothing" {
            let store, scope, documentId = begun "a" 3

            let advance chunk =
                run (store.Advance(report scope documentId (Some "a") chunk))

            Expect.isTrue (isTerminal (advance (ChunkReport.Failed("c0", "dead-lettered")))) "failure is terminal"
            Expect.equal (advance (ChunkReport.Indexed "c1")) AttemptTransition.Dropped "never un-fails"
        }

        test "a callback of a superseded attempt is dropped" {
            let store, scope, documentId = begun "a" 1
            run (store.Begin(scope, documentId, "b", 1))

            Expect.equal
                (run (store.Advance(report scope documentId (Some "a") (ChunkReport.Indexed "c0"))))
                AttemptTransition.Dropped
                "attempt a was superseded by b"

            Expect.isTrue
                (isTerminal (run (store.Advance(report scope documentId (Some "b") (ChunkReport.Indexed "c0")))))
                "attempt b counts its own chunk"
        }

        test "a callback without an attempt counts against the current one" {
            let store, scope, documentId = begun "a" 1

            Expect.isTrue
                (isTerminal (run (store.Advance(report scope documentId None (ChunkReport.Indexed "c0")))))
                "an unattributed chunk advances the current attempt"
        }

        test "an attempt is invisible from another scope" {
            let store, _, documentId = begun "a" 1

            Expect.equal
                (run (store.Advance(report (fresh "team") documentId (Some "a") (ChunkReport.Indexed "c0"))))
                AttemptTransition.Dropped
                "scope isolation"
        }

        test "a forgotten attempt is dropped, and forgetting is idempotent" {
            let store, scope, documentId = begun "a" 1
            run (store.Forget(scope, documentId))
            run (store.Forget(scope, documentId))

            Expect.equal
                (run (store.Advance(report scope documentId (Some "a") (ChunkReport.Indexed "c0"))))
                AttemptTransition.Dropped
                "nothing left to advance"
        }
    ]
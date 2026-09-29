module ToolUp.Platform.Tests.Contracts.ISparseIndexContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.ISparseIndex

// ─── Phase 893 — ISparseIndex contract pack ──────────────────────────
//
// The conformance bar for the keyword leg of hybrid retrieval. Until Phase
// 893 there was exactly one implementation (the in-process BM25 index), so
// its behaviour WAS the contract and nothing wrote it down; a second,
// database-backed implementation makes it a claim that has to hold twice.
// What the pack pins, each a way a keyword index can be wrong while still
// "finding things":
//
//   1. **Scope isolation (GP 4).** A chunk is reachable only through its own
//      scope. The same chunk id in two scopes is two chunks; a delete, a
//      scope wipe or an erasure in one scope never touches the other.
//   2. **Deletes stay deleted.** A deleted chunk is never returned again —
//      not by the next search, and not after the index is re-opened over
//      the same backing (a restart). The lexical index has no soft-delete
//      tier, so its "tombstone" is absence; re-upserting the chunk is what
//      brings it back.
//   3. **The ordering of equal scores.** Equal scores come back ordered by
//      `(Scope, ChunkId)` — `VectorScope`'s structural order, then the
//      ordinal chunk id — never by insertion, hash or storage order. Fusion
//      reads ranks, so a tie broken differently on two calls (or by two
//      implementations) is a different fused answer.
//
// Plus the shape guards around them: upsert is idempotent on
// `(scope, chunkId)`, `topK` caps the result, an unanalysable query and an
// empty scope list return nothing, metadata round-trips, and erasure
// honours `dryRun` and the subject-match contract.
//
// **Scoring is deliberately NOT pinned.** Implementations rank with their
// own function (BM25 in process, the database's ranking function in the
// PostgreSQL companion) on their own scale; the pack asserts only what every
// ranking function agrees on — a chunk that does not contain a query term
// is not returned, and one that does is.
//
// **Binding.** `tests` is the entry point; each implementation binds it
// from its own test file with a harness factory. Every case opens its own
// harness and uses scopes unique to the case, so a binding over a shared
// database needs no cleanup between cases and no two cases contend.

/// One fixture for one case. `Open` opens an index over the fixture's
/// backing and may be called again — after `Close` on the previous one —
/// to model a restart: whatever the implementation persists must be what
/// the re-opened index serves. `Dispose` tears the backing down.
type SparseIndexHarness = {
    Open: unit -> ISparseIndex
    Close: ISparseIndex -> unit
    Dispose: unit -> unit
}

let private chunk (content: string) : TextChunk = {
    Content = content
    Metadata = Map.empty
}

let private chunkWith (content: string) (metadata: (string * string) list) : TextChunk = {
    Content = content
    Metadata = Map.ofList metadata
}

/// Scopes unique to one case, so a binding over a shared backend neither
/// sees a sibling case's rows nor needs cleanup.
let private freshScopes () =
    let id = Guid.NewGuid().ToString "N"
    Team("contract-a-" + id), Team("contract-b-" + id), User("contract-u-" + id)

let private ids (matches: VectorMatch list) = matches |> List.map _.ChunkId

/// Run one case. The body receives the open index and `reopen`, which
/// closes whichever index is current and opens another over the same
/// backing (a restart); the harness closes the current one at the end.
let private withHarness
    (factory: unit -> SparseIndexHarness)
    (body: (unit -> ISparseIndex) -> ISparseIndex -> Async<unit>)
    =
    async {
        let harness = factory ()
        let mutable current = harness.Open()

        let reopen () =
            harness.Close current
            current <- harness.Open()
            current

        try
            do! body reopen current
        finally
            try
                harness.Close current
            finally
                harness.Dispose()
    }

/// The contract pack. `name` labels the implementation; `factory` builds a
/// fresh harness per case.
let tests (name: string) (factory: unit -> SparseIndexHarness) =
    let case label body =
        testCaseAsync label (withHarness factory body)

    testList (sprintf "ISparseIndex contract — %s" name) [

        testList "retrieval" [
            case "a chunk containing a query term is found; one containing none is not"
            <| fun _ index -> async {
                let a, _, _ = freshScopes ()
                do! index.Upsert a "c1" (chunk "quarterly revenue grew in the northern region")
                do! index.Upsert a "c2" (chunk "the office picnic is on friday")
                let! hits = index.Search [ a ] "revenue" 10
                Expect.equal (ids hits) [ "c1" ] "only the chunk containing the term"
                Expect.isTrue (hits |> List.forall (fun m -> m.Scope = a)) "matches carry their scope"
                Expect.isTrue (hits |> List.forall (fun m -> m.Score > 0.0)) "a match scores above zero"
            }

            case "any query term matches — a chunk need not contain every term"
            <| fun _ index -> async {
                let a, _, _ = freshScopes ()
                do! index.Upsert a "c1" (chunk "invoice overdue for customer")
                do! index.Upsert a "c2" (chunk "shipment delayed at port")
                let! hits = index.Search [ a ] "invoice shipment" 10
                Expect.equal (ids hits |> List.sort) [ "c1"; "c2" ] "each chunk matches one of the two terms"
            }

            case "content and metadata round-trip through a search"
            <| fun _ index -> async {
                let a, _, _ = freshScopes ()

                let original =
                    chunkWith "warehouse inventory audit" [ "_origin", "Document"; "docId", "d-7" ]

                do! index.Upsert a "c1" original
                let! hits = index.Search [ a ] "inventory" 10
                let hit = List.exactlyOne hits
                Expect.equal hit.Content original.Content "content is returned verbatim"
                Expect.equal hit.Metadata original.Metadata "metadata is returned verbatim"
            }

            case "upsert is idempotent on (scope, chunkId) — a replace drops the old terms"
            <| fun _ index -> async {
                let a, _, _ = freshScopes ()
                do! index.Upsert a "c1" (chunk "alpha bravo")
                do! index.Upsert a "c1" (chunk "charlie delta")
                let! old = index.Search [ a ] "alpha" 10
                let! current = index.Search [ a ] "charlie" 10
                Expect.isEmpty old "the replaced content no longer matches"
                Expect.equal (ids current) [ "c1" ] "one chunk, with the new content"
            }

            case "topK caps the result; zero, an empty scope list and an unanalysable query return nothing"
            <| fun _ index -> async {
                let a, _, _ = freshScopes ()

                for i in 1..5 do
                    do! index.Upsert a (sprintf "c%d" i) (chunk "ledger reconciliation")

                let! three = index.Search [ a ] "ledger" 3
                let! zero = index.Search [ a ] "ledger" 0
                let! noScopes = index.Search [] "ledger" 10
                let! punctuation = index.Search [ a ] "!!! ??? ..." 10
                Expect.equal three.Length 3 "topK = 3 returns three"
                Expect.isEmpty zero "topK = 0 returns nothing"
                Expect.isEmpty noScopes "no scopes returns nothing"
                Expect.isEmpty punctuation "a query with no terms returns nothing"
            }
        ]

        testList "scope isolation (GP 4)" [
            case "a chunk is reachable only through its own scope"
            <| fun _ index -> async {
                let a, b, _ = freshScopes ()
                do! index.Upsert a "c1" (chunk "confidential merger terms")
                let! fromB = index.Search [ b ] "merger" 10
                let! fromA = index.Search [ a ] "merger" 10
                Expect.isEmpty fromB "another scope never sees it"
                Expect.equal (ids fromA) [ "c1" ] "its own scope does"
            }

            case "the same chunk id in two scopes is two chunks"
            <| fun _ index -> async {
                let a, b, _ = freshScopes ()
                do! index.Upsert a "shared" (chunk "apples from scope a")
                do! index.Upsert b "shared" (chunk "apples from scope b")
                let! both = index.Search [ a; b ] "apples" 10
                Expect.equal both.Length 2 "one match per scope"

                Expect.equal
                    (both |> List.map (fun m -> m.Scope, m.Content) |> Set.ofList)
                    (set [ a, "apples from scope a"; b, "apples from scope b" ])
                    "each match carries its own scope's content"
            }

            case "DeleteChunk and DeleteByScope in one scope leave the other untouched"
            <| fun _ index -> async {
                let a, b, u = freshScopes ()
                do! index.Upsert a "shared" (chunk "turbine maintenance log")
                do! index.Upsert b "shared" (chunk "turbine maintenance log")
                do! index.Upsert u "other" (chunk "turbine maintenance log")
                do! index.DeleteChunk a "shared"
                let! afterChunk = index.Search [ b ] "turbine" 10
                Expect.equal (ids afterChunk) [ "shared" ] "b's same-id chunk survives a's DeleteChunk"
                do! index.DeleteByScope b
                let! afterScope = index.Search [ u ] "turbine" 10
                Expect.equal (ids afterScope) [ "other" ] "a scope wipe leaves other scopes intact"
            }
        ]

        testList "deletes stay deleted" [
            case "a deleted chunk is never returned, and a re-upsert reinstates it"
            <| fun _ index -> async {
                let a, _, _ = freshScopes ()
                do! index.Upsert a "c1" (chunk "pension scheme contribution")
                do! index.Upsert a "c2" (chunk "pension scheme withdrawal")
                do! index.DeleteChunk a "c1"
                let! afterDelete = index.Search [ a ] "pension" 10
                Expect.equal (ids afterDelete) [ "c2" ] "the deleted chunk is gone"
                do! index.Upsert a "c1" (chunk "pension scheme contribution")
                let! afterReupsert = index.Search [ a ] "pension" 10
                Expect.equal (ids afterReupsert |> List.sort) [ "c1"; "c2" ] "re-upserting brings it back"
            }

            case "a DeleteChunk survives re-opening the index over the same backing"
            <| fun reopen index -> async {
                let a, _, _ = freshScopes ()
                do! index.Upsert a "c1" (chunk "freight tariff schedule")
                do! index.Upsert a "c2" (chunk "freight tariff appeal")
                do! index.DeleteChunk a "c1"
                let reopened = reopen ()
                let! hits = reopened.Search [ a ] "freight" 10
                Expect.equal (ids hits) [ "c2" ] "the deletion persisted; the live chunk did too"
            }

            case "a DeleteByScope survives re-opening the index over the same backing"
            <| fun reopen index -> async {
                let a, b, _ = freshScopes ()
                do! index.Upsert a "c1" (chunk "estate planning memo")
                do! index.Upsert b "c1" (chunk "estate planning memo")
                do! index.DeleteByScope a
                let reopened = reopen ()
                let! fromA = reopened.Search [ a ] "estate" 10
                let! fromB = reopened.Search [ b ] "estate" 10
                Expect.isEmpty fromA "the wiped scope stays empty after a restart"
                Expect.equal (ids fromB) [ "c1" ] "the other scope is intact after a restart"
            }
        ]

        testList "ordering of equal scores" [
            case "equal scores within a scope order by chunk id, not by insertion"
            <| fun _ index -> async {
                let a, _, _ = freshScopes ()

                // Identical content ⇒ identical score under any ranking
                // function; inserted out of order on purpose.
                for chunkId in [ "c-3"; "c-10"; "c-1"; "C-2"; "c-2" ] do
                    do! index.Upsert a chunkId (chunk "harbour dredging permit")

                let! hits = index.Search [ a ] "dredging" 10
                let expected = [ "C-2"; "c-1"; "c-10"; "c-2"; "c-3" ]
                Expect.equal (ids hits) expected "ordinal chunk-id order breaks the tie"

                Expect.equal (hits |> List.map _.Score |> List.distinct |> List.length) 1 "the fixture really is a tie"
            }

            case "equal scores across scopes order by scope, then chunk id; and the order is stable"
            <| fun _ index -> async {
                let a, b, u = freshScopes ()
                // The same corpus in every scope, so every per-scope ranking
                // statistic is identical and the scores tie across scopes.
                for scope in [ u; b; a ] do
                    for chunkId in [ "z"; "m" ] do
                        do! index.Upsert scope chunkId (chunk "lighthouse keeper rota")

                let! first = index.Search [ u; b; a ] "lighthouse" 10
                let! second = index.Search [ a; u; b ] "lighthouse" 10

                let expected =
                    [ a; b; u ]
                    |> List.sort
                    |> List.collect (fun scope -> [ scope, "m"; scope, "z" ])

                Expect.equal (first |> List.map (fun m -> m.Scope, m.ChunkId)) expected "(Scope, ChunkId) order"
                Expect.equal (ids second) (ids first) "the requested scope order does not change the result"
            }

            case "a higher score still ranks above a tie-break"
            <| fun _ index -> async {
                let a, _, _ = freshScopes ()
                do! index.Upsert a "a-weak" (chunk "budget review agenda notes minutes attendees")
                do! index.Upsert a "z-strong" (chunk "budget budget budget")
                let! hits = index.Search [ a ] "budget" 10
                Expect.equal (ids hits) [ "z-strong"; "a-weak" ] "score first, chunk id only on a tie"
            }
        ]

        testList "erasure" [
            case "dryRun counts without deleting; the erase removes content and metadata matches in that scope only"
            <| fun _ index -> async {
                let a, b, _ = freshScopes ()
                do! index.Upsert a "c1" (chunk "ticket raised by subject-x7 about billing")
                do! index.Upsert a "c2" (chunkWith "billing ticket escalated" [ "author", "subject-x7" ])
                do! index.Upsert a "c3" (chunk "billing ticket closed")
                do! index.Upsert b "c1" (chunk "ticket raised by subject-x7 about billing")

                let! dry = index.Erase(a, "subject-x7", ErasurePolicy.HardDelete, true)

                match dry with
                | Ok summary -> Expect.equal summary.RecordsAffected 2 "dry run counts content and metadata matches"
                | Error e -> failtestf "dry run failed: %A" e

                let! beforeErase = index.Search [ a ] "billing" 10
                Expect.equal beforeErase.Length 3 "a dry run deletes nothing"

                let! erased = index.Erase(a, "subject-x7", ErasurePolicy.HardDelete, false)

                match erased with
                | Ok summary -> Expect.equal summary.RecordsAffected 2 "both matches erased"
                | Error e -> failtestf "erase failed: %A" e

                let! fromA = index.Search [ a ] "billing" 10
                let! fromB = index.Search [ b ] "billing" 10
                Expect.equal (ids fromA) [ "c3" ] "only the non-matching chunk remains"
                Expect.equal (ids fromB) [ "c1" ] "another scope's chunk naming the subject is untouched"
            }

            case "a blank subject is a no-op"
            <| fun _ index -> async {
                let a, _, _ = freshScopes ()
                do! index.Upsert a "c1" (chunk "anything at all")
                let! result = index.Erase(a, "  ", ErasurePolicy.HardDelete, false)

                match result with
                | Ok summary -> Expect.equal summary.RecordsAffected 0 "nothing affected"
                | Error e -> failtestf "blank erase failed: %A" e

                let! hits = index.Search [ a ] "anything" 10
                Expect.equal (ids hits) [ "c1" ] "nothing deleted"
            }
        ]
    ]
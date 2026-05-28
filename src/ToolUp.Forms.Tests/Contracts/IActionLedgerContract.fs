module ToolUp.Forms.Tests.Contracts.IActionLedgerContract

open Expecto
open ToolUp.Forms.IActionLedger

// ─── IActionLedger contract pack ─────────────────────────────────────
//
// Framework-agnostic test pack: factory builds a fresh `IActionLedger`
// per test. Tests cover the four ledger lifecycle transitions
// (Record → Lookup → MarkSucceeded / MarkFailed) plus the
// composite-key isolation invariants any backing store must satisfy
// — same shape as `IFormStoreContract` / `IWorkflowEngineContract`.
//
// A distributed companion (Postgres-backed ledger, EntityStore-backed
// ledger, etc.) binds the same pack against its own factory and
// proves portability against the same conformance bar as the default
// `InMemoryActionLedger`.

type LedgerFactory = unit -> IActionLedger

let private makeEntry submissionId transitionId actionName status : ActionLedgerEntry = {
    SubmissionId = submissionId
    TransitionId = transitionId
    ActionName = actionName
    Status = status
}

let tests (label: string) (factory: LedgerFactory) =
    testList (sprintf "IActionLedger contract — %s" label) [

        testAsync "Lookup of unknown key returns Ok None" {
            let ledger = factory ()
            let! r = ledger.Lookup("s1", "new:submit:reviewing", "send-email")

            match r with
            | Ok None -> ()
            | other -> failwithf "expected Ok None, got %A" other
        }

        testAsync "Record then Lookup round-trips the entry verbatim" {
            let ledger = factory ()
            let entry = makeEntry "s1" "new:submit:reviewing" "send-email" Pending
            let! recordResult = ledger.Record entry
            Expect.isOk recordResult "Record returned Ok"

            let! lookupResult = ledger.Lookup("s1", "new:submit:reviewing", "send-email")

            match lookupResult with
            | Ok(Some got) ->
                Expect.equal got.SubmissionId "s1" "submission id round-trips"
                Expect.equal got.TransitionId "new:submit:reviewing" "transition id round-trips"
                Expect.equal got.ActionName "send-email" "action name round-trips"
                Expect.equal got.Status Pending "status round-trips"
            | other -> failwithf "expected Ok (Some _), got %A" other
        }

        testAsync "Record is idempotent — second Record of same key is a no-op (status unchanged)" {
            let ledger = factory ()
            let pendingEntry = makeEntry "s2" "submitted:approve:approved" "notify" Pending
            do! ledger.Record pendingEntry |> Async.Ignore

            // Re-record with a different (Succeeded) status — Record
            // must NOT overwrite; the engine uses MarkSucceeded /
            // MarkFailed to mutate, never Record.
            let succeededEntry = { pendingEntry with Status = Succeeded }
            let! r = ledger.Record succeededEntry
            Expect.isOk r "second Record returned Ok"

            let! lookupResult =
                ledger.Lookup(pendingEntry.SubmissionId, pendingEntry.TransitionId, pendingEntry.ActionName)

            match lookupResult with
            | Ok(Some got) -> Expect.equal got.Status Pending "Record did not overwrite the existing entry"
            | other -> failwithf "expected Ok (Some _), got %A" other
        }

        testAsync "MarkSucceeded transitions Pending → Succeeded" {
            let ledger = factory ()
            let entry = makeEntry "s3" "new:start:running" "kick-off" Pending
            do! ledger.Record entry |> Async.Ignore

            let! markResult = ledger.MarkSucceeded(entry.SubmissionId, entry.TransitionId, entry.ActionName)
            Expect.isOk markResult "MarkSucceeded returned Ok"

            let! lookupResult = ledger.Lookup(entry.SubmissionId, entry.TransitionId, entry.ActionName)

            match lookupResult with
            | Ok(Some got) -> Expect.equal got.Status Succeeded "status flipped to Succeeded"
            | other -> failwithf "expected Ok (Some _), got %A" other
        }

        testAsync "MarkFailed transitions Pending → Failed with reason" {
            let ledger = factory ()
            let entry = makeEntry "s4" "x:y:z" "email" Pending
            do! ledger.Record entry |> Async.Ignore

            let! markResult = ledger.MarkFailed(entry.SubmissionId, entry.TransitionId, entry.ActionName, "smtp 421")
            Expect.isOk markResult "MarkFailed returned Ok"

            let! lookupResult = ledger.Lookup(entry.SubmissionId, entry.TransitionId, entry.ActionName)

            match lookupResult with
            | Ok(Some got) ->
                match got.Status with
                | Failed reason -> Expect.equal reason "smtp 421" "reason captured"
                | other -> failwithf "expected Failed, got %A" other
            | other -> failwithf "expected Ok (Some _), got %A" other
        }

        testAsync "MarkSucceeded against unknown key returns Error EntryMissing" {
            let ledger = factory ()
            let! r = ledger.MarkSucceeded("nope", "x:y:z", "never-recorded")

            match r with
            | Error EntryMissing -> ()
            | other -> failwithf "expected Error EntryMissing, got %A" other
        }

        testAsync "MarkFailed against unknown key returns Error EntryMissing" {
            let ledger = factory ()
            let! r = ledger.MarkFailed("nope", "x:y:z", "never-recorded", "any")

            match r with
            | Error EntryMissing -> ()
            | other -> failwithf "expected Error EntryMissing, got %A" other
        }

        testAsync "Composite-key isolation: different (submissionId, transitionId, actionName) tuples don't collide" {
            let ledger = factory ()
            let a = makeEntry "s1" "a:b:c" "act-1" Pending
            let b = makeEntry "s1" "a:b:c" "act-2" Pending // same submission + transition, different action
            let c = makeEntry "s2" "a:b:c" "act-1" Pending // same transition + action, different submission
            let d = makeEntry "s1" "x:y:z" "act-1" Pending // same submission + action, different transition
            do! ledger.Record a |> Async.Ignore
            do! ledger.Record b |> Async.Ignore
            do! ledger.Record c |> Async.Ignore
            do! ledger.Record d |> Async.Ignore

            // Mark only `a` as Succeeded; the other three must stay Pending.
            do!
                ledger.MarkSucceeded(a.SubmissionId, a.TransitionId, a.ActionName)
                |> Async.Ignore

            let! ra = ledger.Lookup(a.SubmissionId, a.TransitionId, a.ActionName)
            let! rb = ledger.Lookup(b.SubmissionId, b.TransitionId, b.ActionName)
            let! rc = ledger.Lookup(c.SubmissionId, c.TransitionId, c.ActionName)
            let! rd = ledger.Lookup(d.SubmissionId, d.TransitionId, d.ActionName)

            let statusOf =
                function
                | Ok(Some(e: ActionLedgerEntry)) -> e.Status
                | other -> failwithf "expected Ok (Some _), got %A" other

            Expect.equal (statusOf ra) Succeeded "a is Succeeded"
            Expect.equal (statusOf rb) Pending "b stays Pending (different actionName)"
            Expect.equal (statusOf rc) Pending "c stays Pending (different submissionId)"
            Expect.equal (statusOf rd) Pending "d stays Pending (different transitionId)"
        }
    ]
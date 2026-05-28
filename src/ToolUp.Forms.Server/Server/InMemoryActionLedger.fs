module ToolUp.Forms.InMemoryActionLedger

open System.Collections.Concurrent
open ToolUp.Forms.FormSubmission
open ToolUp.Forms.IActionLedger

// ─── Phase 21d — Default in-memory IActionLedger ────────────────────
//
// **Single-instance only.** Module-level `ConcurrentDictionary` keyed
// by `(SubmissionId, transitionId, actionName)`. Survives in-process
// retries (the engine writes `Pending` before invoking and reads it
// back on every `Apply` so a same-process retry sees the prior state)
// but does not survive a process restart. Distributed deployments
// MUST wire an `IActionLedger` impl backed by their `IEntityStore` (or
// another durable store) — the in-memory default is for dev /
// single-process tier deployments only.
//
// **Why ConcurrentDictionary, not an immutable map.** The ledger must
// stay coherent under concurrent transitions on different submissions
// hitting the same engine instance; the engine itself is allocated
// once and shared across requests. The dictionary's per-key atomicity
// is sufficient — each call carries the full composite key, and
// `AddOrUpdate` collapses the race between `Lookup` + `Record` /
// `MarkSucceeded` / `MarkFailed`.

type InMemoryActionLedger() =
    let store = ConcurrentDictionary<string, ActionLedgerEntry>()

    let key (submissionId: SubmissionId) (transitionId: string) (actionName: string) : string =
        sprintf "%s|%s|%s" submissionId transitionId actionName

    /// Snapshot of every entry currently in the ledger. Used by tests.
    member _.Entries: ActionLedgerEntry list = store.Values |> List.ofSeq

    interface IActionLedger with

        member _.Record(entry) = async {
            let k = key entry.SubmissionId entry.TransitionId entry.ActionName
            // GetOrAdd — first insert wins; concurrent Record calls
            // for the same key are idempotent (the engine treats the
            // existing entry's Status as authoritative on next Lookup).
            store.GetOrAdd(k, entry) |> ignore
            return Ok()
        }

        member _.Lookup(submissionId, transitionId, actionName) = async {
            let k = key submissionId transitionId actionName

            match store.TryGetValue k with
            | true, entry -> return Ok(Some entry)
            | _ -> return Ok None
        }

        member _.MarkSucceeded(submissionId, transitionId, actionName) = async {
            let k = key submissionId transitionId actionName

            match store.TryGetValue k with
            | true, entry ->
                store[k] <- { entry with Status = Succeeded }
                return Ok()
            | _ -> return Error EntryMissing
        }

        member _.MarkFailed(submissionId, transitionId, actionName, reason) = async {
            let k = key submissionId transitionId actionName

            match store.TryGetValue k with
            | true, entry ->
                store[k] <- { entry with Status = Failed reason }
                return Ok()
            | _ -> return Error EntryMissing
        }
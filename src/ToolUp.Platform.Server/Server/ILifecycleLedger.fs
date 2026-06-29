// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text
open System.Text.Json.Nodes
open ToolUp.Platform.BlobStorage

// ─── Phase 54b — completed-step offboard ledger ──────────────────────
//
// Records which lifecycle hooks have already reached a terminal-success
// disposition (`Completed` / `Skipped`) for a `(scopeId, phase)` run, so
// a re-run (a re-dispatched background job, or a re-issued offboard)
// skips them and re-invokes only the genuinely-incomplete hooks —
// idempotent recovery for a destructive, occasionally-failing operation.
// A `Failed` hook is NOT recorded, so the next run retries it.
//
// Phase 54a shipped a blob-backed completed-hook record inline in
// `LifecycleJobHandler`; this interface lifts that into a portable seam
// the aggregator's resumable sweep consults, so a distributed deployment
// can swap a DB/Redis-backed ledger for the blob default without
// touching the sweep. The aggregator owns the *retry* policy as data
// (`LifecycleRetryPolicy`); the ledger owns *resumability*.
//
// **Six-rule portability audit (Phase 9c, GP 12):**
//   1. Identity by value      — `scopeId` / hook name are strings; the
//                               phase is a DU of value cases. No live
//                               handle crosses the boundary.
//   2. Async at every boundary — every method returns `Async<_>`.
//   3. Retry/supervision as data — the ledger records dispositions; retry
//                                  lives in `LifecycleRetryPolicy`, not a
//                                  callback.
//   4. Stateless between calls — the blob default holds no in-memory
//                                state; every call round-trips to
//                                `IBlobStorage`, so any replica reads what
//                                any other wrote.
//   5. No cross-shard ordering — keyed per `(scopeId, phase)`; no ordering
//                                across scopes.
//   6. Precision at lower bound — no timing primitive; n/a.

/// Terminal-success disposition recorded for a hook. A `Failed` hook is
/// never recorded (it must re-run), so the ledger only carries these two.
[<RequireQualifiedAccess>]
type LedgerDisposition =
    /// The hook ran its work to completion.
    | Completed
    /// The hook's substrate was inactive, so it deliberately did nothing.
    | Skipped

/// Records hooks that have reached a terminal-success disposition for a
/// `(scopeId, phase)` offboard run, so a re-run skips them. Registered as
/// a DI singleton when `ServerConfig.TenantLifecycle = EnabledTenantLifecycle`;
/// the blob-backed default is the zero-dependency floor.
type ILifecycleLedger =
    /// The set of hook names already recorded `Completed`/`Skipped` for
    /// this `(scopeId, phase)`. Empty when no run has recorded anything
    /// (a fresh sweep). Stateless — reads the durable backing every call.
    abstract GetCompleted: scopeId: string * phase: TenantLifecyclePhase -> Async<Set<string>>

    /// Record `hookName` as having reached `disposition` for this
    /// `(scopeId, phase)`. Idempotent — recording the same hook twice is a
    /// no-op. Best-effort from the sweep's perspective: a ledger-write
    /// failure costs resumability for that hook (it re-runs), never the
    /// offboard.
    abstract Record:
        scopeId: string * phase: TenantLifecyclePhase * hookName: string * disposition: LedgerDisposition -> Async<unit>

    /// Clear the ledger for this `(scopeId, phase)` — a clean offboard
    /// finish (so a later re-offboard starts fresh), or a fresh provision
    /// of the scope (re-onboarding supersedes a prior offboard ledger).
    /// Idempotent.
    abstract Clear: scopeId: string * phase: TenantLifecyclePhase -> Async<unit>

// ─── blob-backed default ─────────────────────────────────────────────

[<AutoOpen>]
module private BlobBackedLifecycleLedgerInternal =
    /// Reserved SDK-level container — the ledger is platform metadata, not
    /// tenant data, so it lives under `_platform`, never a tenant scope
    /// (and is therefore never swept by the offboard's own data-erasure
    /// hook). Mirrors `ILifecycleSummaryStore`'s container choice.
    let ledgerContainer = "_platform"

    /// `scopeId` + phase form a blob name. Reject path separators (they
    /// would escape the container prefix); coerce other non-safe chars to
    /// `_`. Scope ids the resolver mints (`team-{guid}` / `user-{id}`)
    /// pass through. Mirrors `BlobBackedLifecycleSummaryStore.sanitiseScopeId`.
    let sanitise (s: string) : string =
        if String.IsNullOrWhiteSpace s then
            raise (ArgumentException("value must be non-empty", nameof s))

        if s.Contains('/') || s.Contains('\\') then
            raise (ArgumentException(sprintf "value must not contain path separators; got %s" s, nameof s))

        s
        |> Seq.map (fun c ->
            if Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.' then
                c
            else
                '_')
        |> Seq.toArray
        |> String

    let blobNameFor (scopeId: string) (phase: TenantLifecyclePhase) : string =
        sprintf "_tenant-lifecycle-ledger/%s/%s.json" (sanitise scopeId) (TenantLifecyclePhase.name phase)

/// Phase 54b — `ILifecycleLedger` backed by `IBlobStorage`. Each
/// `(scope, phase)` run's recorded hook set lives at
/// `_platform/_tenant-lifecycle-ledger/{scopeId}/{phase}.json` as a flat
/// JSON array of hook names. Cluster-portable: any blob backend works and
/// every node reads the same recorded set.
type BlobBackedLifecycleLedger(blobs: IBlobStorage) =
    let blobName = blobNameFor

    interface ILifecycleLedger with
        member _.GetCompleted(scopeId: string, phase: TenantLifecyclePhase) : Async<Set<string>> = async {
            match! blobs.Download(ledgerContainer, blobName scopeId phase) with
            | Error _ -> return Set.empty
            | Ok bytes ->
                try
                    let arr = JsonNode.Parse(Encoding.UTF8.GetString bytes).AsArray()
                    return arr |> Seq.map (fun n -> n.GetValue<string>()) |> Set.ofSeq
                with _ ->
                    // A corrupt ledger reads as "nothing recorded" — a full
                    // re-run, which is safe because the first-party hooks
                    // are idempotent.
                    return Set.empty
        }

        member this.Record
            (scopeId: string, phase: TenantLifecyclePhase, hookName: string, _disposition: LedgerDisposition)
            : Async<unit> =
            async {
                // Read-modify-write. The resumable sweep records one hook at
                // a time, sequentially, so this does not race itself within a
                // run. Both dispositions are "done"; the disposition is kept
                // in the contract for callers/telemetry but the stored shape
                // is just the done-set (a re-run skips either disposition).
                let! current = (this :> ILifecycleLedger).GetCompleted(scopeId, phase)
                let updated = Set.add hookName current
                let arr = JsonArray()

                for name in updated do
                    arr.Add(JsonValue.Create name)

                let bytes = Encoding.UTF8.GetBytes(arr.ToJsonString())
                let! _ = blobs.Upload(ledgerContainer, blobName scopeId phase, bytes)
                return ()
            }

        member _.Clear(scopeId: string, phase: TenantLifecyclePhase) : Async<unit> = async {
            let! _ = blobs.Delete(ledgerContainer, blobName scopeId phase)
            return ()
        }

module BlobBackedLifecycleLedger =
    /// Construct an `ILifecycleLedger` backed by the given `IBlobStorage`.
    let create (blobs: IBlobStorage) : ILifecycleLedger =
        BlobBackedLifecycleLedger(blobs) :> ILifecycleLedger
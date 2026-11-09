// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Compose

open ToolUp.Platform

open System
open System.Text
open System.Text.Json.Nodes
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra

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
/// a DI singleton when `ServerConfig.TenantLifecycle = FeatureMode.Enabled`;
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

    /// The ledger is platform metadata, not tenant data, so it lives in the
    /// reserved `_platform` container, never a tenant scope (and is
    /// therefore never swept by the offboard's own data-erasure hook).
    /// Mirrors `ILifecycleSummaryStore`'s container choice.
    let pathFor (scopeId: string) (phase: TenantLifecyclePhase) : DocPath =
        DocPath.ofString (
            sprintf "_tenant-lifecycle-ledger/%s/%s.json" (sanitise scopeId) (TenantLifecyclePhase.name phase)
        )

    /// A flat JSON array of hook names, the bytes the ledger has always
    /// written (a thin codec: the shape predates `PlatformJson`).
    let codec: BlobCodec<Set<string>> = {
        Encode =
            fun names ->
                let arr = JsonArray()

                for name in names do
                    arr.Add(JsonValue.Create name)

                Encoding.UTF8.GetBytes(arr.ToJsonString())
        Decode =
            fun bytes ->
                try
                    JsonNode.Parse(Encoding.UTF8.GetString bytes).AsArray()
                    |> Seq.map (fun n -> n.GetValue<string>())
                    |> Set.ofSeq
                    |> Ok
                with ex ->
                    Error ex.Message
    }

/// Phase 54b — `ILifecycleLedger` backed by `IBlobStorage`. Each
/// `(scope, phase)` run's recorded hook set lives at
/// `_platform/_tenant-lifecycle-ledger/{scopeId}/{phase}.json` as a flat
/// JSON array of hook names. Cluster-portable: any blob backend works and
/// every node reads the same recorded set.
///
/// A ledger that cannot be read or decoded reads as "nothing recorded" —
/// a full re-run, which is safe because the first-party hooks are
/// idempotent — and says so in the log. `Record` is one guarded update:
/// a read failure writes nothing and raises (it used to read as empty and
/// write the one hook over everything recorded), and an undecodable
/// ledger is set aside and started again rather than wedging the sweep.
type BlobBackedLifecycleLedger(blobs: IBlobStorage) =
    let logger = JsonConsoleLogger.JsonConsoleLogger() :> ILogger
    let docs = BlobDocumentStore<Set<string>>(blobs, codec, logger)

    interface ILifecycleLedger with
        member _.GetCompleted(scopeId: string, phase: TenantLifecyclePhase) : Async<Set<string>> = async {
            match! docs.Read(ScopeKey.platform, pathFor scopeId phase) with
            | Ok recorded -> return Option.defaultValue Set.empty recorded
            | Error e ->
                logger.Warn(
                    $"[LifecycleLedger] {BlobMapStoreError.describe e}; the {TenantLifecyclePhase.name phase} run for {scopeId} re-runs every hook"
                )

                return Set.empty
        }

        member _.Record
            (scopeId: string, phase: TenantLifecyclePhase, hookName: string, _disposition: LedgerDisposition)
            : Async<unit> =
            async {
                // Both dispositions are "done"; the disposition is kept in
                // the contract for callers/telemetry but the stored shape is
                // just the done-set (a re-run skips either disposition).
                let! recorded =
                    docs.UpdateOrRestart(
                        ScopeKey.platform,
                        pathFor scopeId phase,
                        fun current ->
                            let names = Option.defaultValue Set.empty current

                            if names.Contains hookName then
                                BlobUpdate.Keep()
                            else
                                BlobUpdate.Write(Set.add hookName names, ())
                    )

                // Phase 863 — the ledger is what a resumed sweep trusts, so a
                // refused write raises rather than reading as recorded; the
                // lifecycle job turns it into a transient failure and the
                // retry resumes from the last hook that WAS recorded.
                match recorded with
                | Ok() -> return ()
                | Error e ->
                    return
                        failwithf
                            "BlobBackedLifecycleLedger: recording hook %s for %s could not be written: %s"
                            hookName
                            scopeId
                            (BlobMapStoreError.describe e)
            }

        member _.Clear(scopeId: string, phase: TenantLifecyclePhase) : Async<unit> = async {
            // Phase 971 — a kept done-set makes a later run of this phase
            // skip every hook it names, so a refused clear raises (as a
            // refused `Record` does) rather than reading as cleared. A
            // missing ledger still answers Ok: Clear stays idempotent.
            match! docs.Delete(ScopeKey.platform, pathFor scopeId phase) with
            | Ok() -> return ()
            | Error e ->
                return
                    failwithf
                        "BlobBackedLifecycleLedger: clearing the %s ledger for %s was refused, so its recorded hooks would be skipped by the next run: %s"
                        (TenantLifecyclePhase.name phase)
                        scopeId
                        (BlobMapStoreError.describe e)
        }

module BlobBackedLifecycleLedger =
    /// Construct an `ILifecycleLedger` backed by the given `IBlobStorage`.
    let create (blobs: IBlobStorage) : ILifecycleLedger =
        BlobBackedLifecycleLedger(blobs) :> ILifecycleLedger
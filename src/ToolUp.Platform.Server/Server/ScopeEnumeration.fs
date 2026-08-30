// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.TeamManagement
open DataManagementTypes

// ─── Phase 723 — scope enumeration seam ───────────────────────────
//
// Several SDK sweeps need the same answer and none of them can compute
// it: *what storage scopes does this deployment hold?* `IBlobStorage`
// has no cross-container enumeration, so the restart-recovery sweeps
// (`RAGServerApp.withIngestionRecoverySweep`, KB's
// `recoverStuckDocumentsAtStartup`) and the retention sweep each take a
// hand-written container list from the consumer.
//
// That is not merely inconvenient — it inverts who carries the risk. A
// deployment large enough to need a restart sweep is exactly the one
// whose container list is long, changes as teams are created, and is
// easiest to get wrong or never write at all; so the sweeps most needed
// are the ones least often wired, and the gap they exist to close stays
// open with a `Pending` badge that nothing will ever clear.
//
// `IScopeEnumerator` is the seam that answers the question once. It is
// deliberately tiny, and deliberately NOT a new `ITeamStore` member:
// adding an abstract member to a shipped interface breaks every external
// implementor, and `ITeamStore.ListTeams` already returns exactly the
// enumeration the default needs. So the default is an ADAPTER over the
// existing surface (`ScopeEnumeration.fromTeamStore`), not an extension
// of it.
//
// **GP 12 — the six portability rules.** A distributed scope directory
// (an external tenant registry, a control-plane API, a Postgres table)
// must be implementable against this interface without changing it:
//
//  1. *Identity by value.* Scopes are `string` container ids — never a
//     live handle, never a `TeamInfo` (which would bind the seam to the
//     SDK's own team model and lock out a directory that has no notion
//     of a team).
//  2. *Async at every boundary.* `ListScopes` returns `Async<string
//     list>`; a remote directory is a round-trip and must not be forced
//     to block. `Name` is a constant label, the same shape
//     `IIngestionQueueStore.Name` and `IAuditSink.Name` already carry.
//  3. *Retry / supervision as data.* The interface carries no callback
//     and no policy object. A failing enumeration surfaces as an
//     exception the CALLER decides about — `IngestionRecoverySweep`
//     logs and sweeps nothing rather than crashing startup.
//  4. *Stateless between invocations.* Every call re-enumerates. No
//     cursor, no handle, nothing carried by the caller between calls, so
//     an implementation may be a grain that deactivates or an actor that
//     restarts between two calls.
//  5. *No cross-shard ordering promise.* The returned list is a SET
//     rendered as a list; order is not part of the contract and callers
//     must not depend on it.
//  6. *Precision at the lower bound.* The result is a SNAPSHOT, not a
//     boundary: a scope created during a sweep may or may not appear.
//     Every consumer in the SDK is idempotent and re-runs, so a missed
//     scope is a deferred visit rather than a lost one.

/// Answers "what storage scopes does this deployment hold?" — the
/// enumeration the SDK's per-scope sweeps need and `IBlobStorage` cannot
/// provide.
///
/// Opt-in and default-absent (GP 13): nothing in the SDK constructs one,
/// so a deployment that composes no enumerator behaves exactly as it did
/// before this seam existed. Compose `ScopeEnumeration.fromTeamStore`
/// (or a companion implementation) to switch the sweeps from
/// hand-enumerated container lists to a live enumeration.
type IScopeEnumerator =
    /// Stable name for logs / health output (e.g. `"team-store"`).
    abstract Name: string

    /// Every storage scope (container id) this deployment holds, as a
    /// snapshot. Unordered; may legitimately be empty. Callers treat a
    /// throw as "enumerate nothing" rather than a fatal condition — see
    /// rule 3 above.
    abstract ListScopes: unit -> Async<string list>

/// Constructors + well-known scope ids for `IScopeEnumerator`.
module ScopeEnumeration =

    /// The platform-wide container (`ITeamStore` metadata, platform
    /// config, platform-scoped documents).
    [<Literal>]
    let PlatformContainer = "_platform"

    /// The deployment-wide container used by deployment-scoped
    /// substrate.
    [<Literal>]
    let DeploymentContainer = "_deployment"

    /// The two well-known containers every deployment may hold
    /// regardless of its team model. Included by `fromTeamStore` because
    /// a consumer hand-writing the list was told to include them (see
    /// the `withIngestionRecoverySweep` / `recoverStuckDocumentsAtStartup`
    /// remarks), so omitting them here would silently narrow the sweep.
    let wellKnownContainers = [ PlatformContainer; DeploymentContainer ]

    /// Storage container id for a team id, matching the `team-{id}`
    /// convention `StorageScope` / `VectorScope` resolve to. Idempotent
    /// — an id that already carries the prefix is returned unchanged, so
    /// a store whose ids are already container-shaped is not
    /// double-prefixed.
    let containerForTeam (teamId: string) : string =
        if teamId.StartsWith("team-", StringComparison.Ordinal) then
            teamId
        else
            "team-" + teamId

    /// A fixed enumeration. The escape hatch for a deployment whose
    /// scopes are genuinely static (a single-tenant appliance, a test),
    /// and the shape the sweeps' pre-723 `string list` argument becomes.
    let ofScopes (name: string) (scopes: string list) : IScopeEnumerator =
        { new IScopeEnumerator with
            member _.Name = name
            member _.ListScopes() = async { return scopes }
        }

    /// The SDK default: every team's container (via the EXISTING
    /// `ITeamStore.ListTeams`, so no interface member is added and no
    /// external implementor breaks) plus `extraScopes`.
    ///
    /// **Archived teams are included deliberately.** An archived team's
    /// documents are still stored, still readable once restored, and a
    /// document left `Pending` in one is exactly as stuck as any other —
    /// filtering them would make the archive a place interrupted
    /// ingestion goes to hide.
    ///
    /// **What this cannot see, stated plainly.** Personal (`user-{id}`)
    /// containers are not enumerable from `ITeamStore` — there is no
    /// user directory in the SDK core to enumerate — so a deployment in
    /// personal mode composes `ofScopes` (or a companion enumerator)
    /// instead. Reporting that honestly is the point: an enumerator that
    /// silently returned only team scopes would look composed while
    /// sweeping none of a personal-mode deployment's documents.
    let fromTeamStoreWith (extraScopes: string list) (teamStore: ITeamStore) : IScopeEnumerator =
        { new IScopeEnumerator with
            member _.Name = "team-store"

            member _.ListScopes() = async {
                let! teams = teamStore.ListTeams()

                let teamContainers = teams |> List.map (fun t -> containerForTeam t.TeamId)

                return (extraScopes @ teamContainers) |> List.distinct
            }
        }

    /// `fromTeamStoreWith wellKnownContainers` — the shape a consumer
    /// was previously told to hand-write.
    let fromTeamStore (teamStore: ITeamStore) : IScopeEnumerator =
        fromTeamStoreWith wellKnownContainers teamStore

// ─── Phase 723 — the converged restart-recovery sweep ─────────────
//
// Before this phase there were TWO restart sweeps: RAG's (over
// `IIngestionStatusStore`, registered by `composeWithRAG` when the
// consumer named containers) and KB's (over the `knowledge/index.json`
// document index, called by the consumer from its composition root).
// Near-identical routines — same filter-for-non-terminal, same
// mark-Failed, same per-container error isolation, same reason string —
// living in two companions.
//
// They cannot become ONE traversal, because they read two genuinely
// different durable surfaces: a KB deployment holds both, and sweeping
// only one leaves the other's badge stuck. What they CAN share, and now
// do, is one implementation: the traversal, the error isolation, the
// reason string, the logging shape and the hosted-service wiring live
// here once, and each companion supplies a small adapter naming its own
// surface. Adding a third surface is an adapter, not a third sweep.

/// One durable per-document ingestion-status surface a restart sweep
/// visits. The narrow half of the convergence: everything ABOUT a sweep
/// (which scopes, error isolation, logging, idempotence) belongs to
/// `IngestionRecoverySweep`; this carries only "which documents in this
/// scope were left mid-ingestion, and how do I mark one interrupted".
///
/// Satisfies the six portability rules for the same reasons
/// `IScopeEnumerator` does — ids by value, `Async` at every boundary, no
/// callbacks, nothing carried between calls, no ordering promise, and a
/// snapshot rather than a boundary.
type IIngestionRecoverySurface =
    /// Stable name for logs (e.g. `"rag-ingestion-status"`).
    abstract Name: string

    /// Document ids in `scope` left in a NON-TERMINAL ingestion status —
    /// the ones a process that died mid-ingestion abandoned. A terminal
    /// status (indexed, already failed, unsupported format, OCR
    /// unavailable) is never returned: re-failing a document that
    /// carries an accurate, actionable status would replace it with a
    /// generic one.
    abstract ListInterrupted: scope: string -> Async<string list>

    /// Mark one document interrupted, with the caller's reason. Called
    /// only for ids this surface itself returned.
    abstract MarkInterrupted: scope: string * documentId: string * reason: string -> Async<unit>

/// The one restart-recovery sweep implementation, shared by every
/// ingestion-status surface (Phase 723.D).
module IngestionRecoverySweep =

    /// The reason written onto every document the sweep marks. One
    /// string, shared — it is user-visible text on a badge, and the two
    /// sweeps carried byte-identical copies of it before they converged.
    [<Literal>]
    let InterruptedReason =
        "Ingestion was interrupted by a process restart before the document finished indexing. Re-upload the file to re-index it."

    /// Adapter for the RAG / Data-Manager per-file status store. `Pending`
    /// is its only non-terminal status.
    let ofIngestionStatusStore (store: IIngestionStatusStore) : IIngestionRecoverySurface =
        { new IIngestionRecoverySurface with
            member _.Name = "rag-ingestion-status"

            member _.ListInterrupted(scope) = async {
                let! entries = store.List scope

                return
                    entries
                    |> List.filter (fun (_, status) -> status = FileIngestionStatus.Pending)
                    |> List.map fst
            }

            member _.MarkInterrupted(scope, documentId, reason) =
                store.Set(scope, documentId, FileIngestionStatus.Failed reason)
        }

    /// The scopes a sweep visits: the explicitly-named containers, plus
    /// whatever the composed enumerator reports.
    ///
    /// Both inputs are honoured rather than one overriding the other,
    /// because they answer different questions — the explicit list is
    /// "containers this deployment knows it holds", the enumerator is
    /// "containers the directory can see" — and a deployment migrating
    /// from the first to the second should not lose coverage in the gap.
    /// An enumerator that throws degrades to the explicit list with a
    /// warning: a directory outage must not take startup down, and must
    /// not silently look like an empty deployment either.
    let resolveScopes
        (explicitScopes: string list)
        (enumerator: IScopeEnumerator option)
        (logger: ILogger)
        : Async<string list> =
        async {
            match enumerator with
            | None -> return explicitScopes |> List.distinct
            | Some e ->
                try
                    let! enumerated = e.ListScopes()
                    return (explicitScopes @ enumerated) |> List.distinct
                with ex ->
                    logger.Error(
                        sprintf
                            "[IngestionRecoverySweep] event=scope_enumeration_failed enumerator=%s: falling back to the %d explicitly-composed scope(s); scopes this enumerator would have added are NOT swept this start."
                            e.Name
                            (List.length explicitScopes),
                        Some ex
                    )

                    return explicitScopes |> List.distinct
        }

    /// Sweep every `surface` over every `scope`, marking each
    /// interrupted document with `InterruptedReason`. Returns the total
    /// marked across all surfaces and scopes.
    ///
    /// **Idempotent.** A document marked on a previous start now carries
    /// a terminal status, so a surface no longer reports it; re-running
    /// the sweep marks nothing.
    ///
    /// **Per-(surface, scope) errors are isolated**, deliberately at
    /// that granularity rather than per-surface: one unreadable
    /// container must not stop the other containers of the same surface,
    /// which is what the pre-convergence sweeps both did.
    let run (surfaces: IIngestionRecoverySurface list) (scopes: string list) (logger: ILogger) : Async<int> = async {
        let mutable total = 0

        for surface in surfaces do
            for scope in scopes do
                try
                    let! stuck = surface.ListInterrupted scope

                    if not stuck.IsEmpty then
                        logger.Warn(
                            sprintf
                                "[IngestionRecoverySweep] event=interrupted_documents_found surface=%s scope=%s count=%d: document(s) left mid-ingestion by a prior process; marking Failed."
                                surface.Name
                                scope
                                stuck.Length
                        )

                        for documentId in stuck do
                            do! surface.MarkInterrupted(scope, documentId, InterruptedReason)
                            total <- total + 1
                with ex ->
                    logger.Error(
                        sprintf
                            "[IngestionRecoverySweep] event=recovery_scan_failed surface=%s scope=%s: skipping this scope."
                            surface.Name
                            scope,
                        Some ex
                    )

        if total > 0 then
            logger.Warn(
                sprintf
                    "[IngestionRecoverySweep] event=recovery_swept count=%d surfaces=%d scopes=%d: document(s) left mid-ingestion by a prior process were marked Failed. Affected uploaders see a Failed badge and can re-upload."
                    total
                    (List.length surfaces)
                    (List.length scopes)
            )

        return total
    }

    /// The startup hosted service both composition roots register.
    ///
    /// Surfaces and scopes are resolved at `StartAsync`, not at compose:
    /// the surface set is whatever the built provider holds (so a
    /// companion registering its own surface is picked up with no
    /// ordering requirement between compose helpers), and the scope
    /// enumeration is a live read (so a team created since the last
    /// start is swept on this one).
    let hostedService
        (logger: ILogger)
        (resolveSurfaces: unit -> IIngestionRecoverySurface list)
        (resolveSweepScopes: unit -> Async<string list>)
        : IHostedService =
        { new IHostedService with
            member _.StartAsync(_ct) =
                async {
                    let surfaces = resolveSurfaces ()

                    if not surfaces.IsEmpty then
                        let! scopes = resolveSweepScopes ()

                        if not scopes.IsEmpty then
                            let! _ = run surfaces scopes logger
                            ()
                }
                |> Async.StartAsTask
                :> System.Threading.Tasks.Task

            member _.StopAsync(_ct) =
                System.Threading.Tasks.Task.CompletedTask
        }
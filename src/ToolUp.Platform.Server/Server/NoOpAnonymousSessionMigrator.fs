// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── NoOpAnonymousSessionMigrator — Phase 66 Stream C.1 (substrate) ──
//
// Default `IAnonymousSessionMigrator` registered by the composition
// root when the application does not call
// `ServerApp.withSubjectMigrator`. Every Migrate call returns
// `Error (MigrationError.NotEligible "no migrator wired")` so the
// trigger-detection path in the middleware (deferred — lands in the
// Stream C.1 continuation) can update `LastSeenAnonymousSessionId`
// uniformly without a None-branch.
//
// The accompanying `AnonymousSessionMigrator.compose` combinator
// folds N per-module migrators into one — runs each sequentially,
// accumulating summaries; bails on the first `InfrastructureFailed`
// (failures here are not retriable on the next item, so the runbook
// is "fix the substrate, retry the whole batch"); collapses
// `NotEligible` to a no-op for that migrator (other migrators in the
// chain may still produce items).

/// Default no-op migrator. Returned by every `Migrate` call with
/// `MigrationError.NotEligible "no migrator wired"`. Composition root
/// registers this when the application does not wire a custom
/// migrator via `ServerApp.withSubjectMigrator`.
type NoOpAnonymousSessionMigrator() =
    interface IAnonymousSessionMigrator with
        member _.Migrate(_anonymousSessionId, _authenticatedSubject) = async {
            return Result.Error(MigrationError.NotEligible "no migrator wired")
        }

module AnonymousSessionMigrator =
    /// Compose a list of migrators into one — runs each sequentially,
    /// accumulating successful summaries; collapses `NotEligible` to
    /// a no-op for that migrator; bails on the first
    /// `InfrastructureFailed` (substrate failures are not retriable
    /// on the next item — the runbook is "fix the substrate, retry
    /// the whole batch"); surfaces `PartialFailure` as a fold of the
    /// preceding summary plus the partial's own counts so the
    /// operator sees the full migrated-vs-failed split.
    ///
    /// Empty list returns `NoOpAnonymousSessionMigrator` for
    /// consistency with the unwired-default case.
    let compose (migrators: IAnonymousSessionMigrator list) : IAnonymousSessionMigrator =
        match migrators with
        | [] -> NoOpAnonymousSessionMigrator() :> IAnonymousSessionMigrator
        | [ single ] -> single
        | many ->
            { new IAnonymousSessionMigrator with
                member _.Migrate(anonymousSessionId, authenticatedSubject) = async {
                    let mutable accSummary = MigrationSummary.empty
                    let mutable bail: Result<MigrationSummary, MigrationError> option = None
                    let mutable remaining = many

                    while bail.IsNone && not (List.isEmpty remaining) do
                        let migrator = List.head remaining
                        remaining <- List.tail remaining
                        let! result = migrator.Migrate(anonymousSessionId, authenticatedSubject)

                        match result with
                        | Ok summary -> accSummary <- MigrationSummary.combine accSummary summary
                        | Result.Error(MigrationError.NotEligible _) ->
                            // This migrator declines for a benign
                            // reason (no data, wrong shape, etc.).
                            // Skip and continue the chain — other
                            // migrators may still contribute.
                            ()
                        | Result.Error(MigrationError.InfrastructureFailed _ as err) ->
                            // Substrate failure — bail. Caller (the
                            // middleware) will not update
                            // `LastSeenAnonymousSessionId`, so the
                            // next request retries the whole chain.
                            bail <- Some(Result.Error err)
                        | Result.Error(MigrationError.PartialFailure(partial, failedItems, lastError)) ->
                            // Partial — fold the partial into the
                            // accumulator and surface the partial
                            // shape unchanged so the caller can
                            // update LastSeenAnonymousSessionId per
                            // OQ5 semantics (no thrash on
                            // non-retriable item failures).
                            let combined = MigrationSummary.combine accSummary partial

                            bail <- Some(Result.Error(MigrationError.PartialFailure(combined, failedItems, lastError)))

                    match bail with
                    | Some err -> return err
                    | None -> return Ok accSummary
                }
            }
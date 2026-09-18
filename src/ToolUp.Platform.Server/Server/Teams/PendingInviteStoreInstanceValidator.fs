// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Teams.PendingInviteStoreInstanceValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── PendingInviteStore single-instance enforcement ──────────────────
//
// `InMemoryPendingInviteStore` (the email-keyed pre-invite blob backing
// `ITeamInviteApi.IssuePendingInviteByEmail`) serialises writes via a
// process-local `SemaphoreSlim` over a full-blob overwrite. In a
// single-instance deployment that is correct. In a multi-replica
// deployment two processes can both load → both `Map.add` → both write —
// last writer wins, intermediate updates silently lost; and each
// process's 30-second in-memory cache can serve a stale read after a
// peer consumed the entry, double-applying the auto-join.
//
// Phase 5h closed that gap: `compose` auto-selects the ETag-based
// `BlobPendingInviteStore` for `ReplicaCount > 1` whenever the composed
// `IBlobStorage` implements `IConditionalBlobStorage` (the local, Azure,
// S3 and GCS backends all do). So the Warning now fires only when the
// RESOLVED store is still the in-memory one under `ReplicaCount > 1` —
// the backend cannot do conditional writes, or a consumer supplied the
// in-memory store explicitly — and the escape hatch remains for the
// deployment that has read the risk and accepts it.
//
// Mirrors the JobSchedulerInstanceValidator / OAuthStateStoreInstance
// Validator shape. Pending-by-email is opt-in by admin action; when no
// entry has ever been written the blob doesn't exist, and a
// multi-instance deployment that doesn't use the feature is unaffected.

/// Config validator that warns when the in-memory `InMemoryPendingInviteStore`
/// is the RESOLVED pending-invite store under `ReplicaCount > 1`. A
/// deployment running `BlobPendingInviteStore` (auto-selected, or supplied
/// via `ServerApp.withPendingInviteStore`) or any other custom store is
/// clean regardless of replica count; the explicit escape hatch
/// `AcceptPendingInviteStoreInMultiInstance` silences the Warning for
/// the in-memory store.
///
/// `resolvedStore = None` is the pre-5h reading — the store is unknown,
/// so it is assumed to be the in-memory default — kept for the
/// `(config, ?timeout)` constructor callers that predate the gate update.
type PendingInviteStoreInstanceValidator
    /// Phase 5h constructor: `resolvedStore` is the `IPendingInviteStore`
    /// `compose` actually registered, so the gate can type-test it.
    (config: ServerConfig, resolvedStore: IPendingInviteStore option, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    /// Pre-5h constructor — no resolved store in hand, so the validator
    /// assumes the in-memory default (the only store that existed then).
    new(config: ServerConfig, ?timeout: TimeSpan)= PendingInviteStoreInstanceValidator(config, None, ?timeout = timeout)

    interface IConfigValidator with
        member _.Name = "pending-invite-store-instance"
        member _.Timeout = timeout

        member _.Validate() = async {
            let multiInstance = config.ReplicaCount > 1
            let escapeHatch = config.AcceptPendingInviteStoreInMultiInstance

            let inMemoryResolved =
                match resolvedStore with
                | None -> true
                | Some store ->
                    match box store with
                    | :? InMemoryPendingInviteStore -> true
                    | _ -> false

            if multiInstance && inMemoryResolved && not escapeHatch then
                return
                    Warning(
                        sprintf
                            "InMemoryPendingInviteStore (email-keyed pending invitations) is single-instance-only, and it is the resolved IPendingInviteStore under ServerConfig.ReplicaCount = %d. Writes serialise on a process-local lock + full-blob overwrite; two replicas issuing pending-by-email entries concurrently will silently lose updates, and a 30-second per-process read cache can serve stale entries that a peer already consumed (double auto-join). The link-based invitation flow is unaffected. The SDK auto-selects the ETag-based BlobPendingInviteStore for ReplicaCount > 1 when the composed IBlobStorage implements IConditionalBlobStorage — compose a blob backend with conditional-write support (the local, Azure, S3 and GCS backends all have it) or supply one via ServerApp.withPendingInviteStore; or avoid the IssuePendingInviteByEmail surface in multi-instance deployments; or set ServerConfig.AcceptPendingInviteStoreInMultiInstance = true (TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE=1) to acknowledge the risk."
                            config.ReplicaCount
                    )
            else
                return Ok
        }
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Teams.PendingInviteStoreInstanceValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── PendingInviteStore single-instance enforcement ──────────────────
//
// `PendingInviteStore` (the email-keyed pre-invite blob backing
// `ITeamInviteApi.IssuePendingInviteByEmail`) serialises writes via a
// process-local `SemaphoreSlim` over a full-blob overwrite. In a
// single-instance deployment that is correct. In a multi-replica
// deployment two processes can both load → both `Map.add` → both write —
// last writer wins, intermediate updates silently lost; and each
// process's 30-second in-memory cache can serve a stale read after a
// peer consumed the entry, double-applying the auto-join. Documented
// as deferred to the Phase 9c half-2 ETag-based optimistic-concurrency
// follow-up — but right now, without enforcement, a multi-instance
// deployment that wants the pending-by-email flow silently corrupts.
//
// Mirrors the JobSchedulerInstanceValidator / OAuthStateStoreInstance
// Validator shape: refuse startup when ReplicaCount > 1 unless an
// explicit escape hatch is set. Pending-by-email is opt-in by admin
// action; when no entry has ever been written the blob doesn't exist,
// and a multi-instance deployment that doesn't use the feature is
// unaffected.

/// Config validator that refuses the in-memory `PendingInviteStore`
/// paired with `ReplicaCount > 1`. Single-instance deployments are
/// unaffected; multi-instance deployments that want the pending-by-
/// email flow must wait for the Phase 9c half-2 follow-up (ETag-based
/// optimistic concurrency on `IBlobStorage.Upload`), or set the
/// explicit escape hatch.
type PendingInviteStoreInstanceValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "pending-invite-store-instance"
        member _.Timeout = timeout

        member _.Validate() = async {
            let multiInstance = config.ReplicaCount > 1
            let escapeHatch = config.AcceptPendingInviteStoreInMultiInstance

            if multiInstance && not escapeHatch then
                return
                    Warning(
                        sprintf
                            "PendingInviteStore (email-keyed pending invitations) is single-instance-only. ServerConfig.ReplicaCount = %d. Writes serialise on a process-local lock + full-blob overwrite; two replicas issuing pending-by-email entries concurrently will silently lose updates, and a 30-second per-process read cache can serve stale entries that a peer already consumed (double auto-join). The link-based invitation flow is unaffected. Either avoid the IssuePendingInviteByEmail surface in multi-instance deployments, wait for the Phase 9c half-2 follow-up (ETag-based optimistic concurrency on IBlobStorage.Upload), or set ServerConfig.AcceptPendingInviteStoreInMultiInstance = true (TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE=1) to acknowledge the risk."
                            config.ReplicaCount
                    )
            else
                return Ok
        }
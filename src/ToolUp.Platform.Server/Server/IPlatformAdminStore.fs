namespace ToolUp.Platform

// ─── IPlatformAdminStore ──────────────────────────────────────────────
//
// Phase 4b — deployment-wide admin role substrate. Persists the set of
// users holding `PlatformRole.PlatformAdmin` and resolves membership
// for the per-request `AccessContext.PlatformRole` middleware lookup.
//
// SDK-default impl: `BlobBackedPlatformAdminStore`
// (`Server/Infra/PlatformAdminStore.fs`) writes a flat list of userIds
// to `_platform/platform-admins.json`. Distributed companions (Redis-
// backed cache, KMS-backed identity stores) bind to this interface.
//
// **Phase 9c portability rules** (all six honoured):
//
//   1. Identity by value. UserIds are strings — no `IActorRef`,
//      `IGrainReference`, or other live handles.
//   2. Async at every boundary. Every method returns `Async<_>`.
//   3. Retry / supervision as data. Failures returned as `Result<_,
//      string>`; no `OnFailure` callbacks.
//   4. Stateless handlers between invocations. Every read re-loads the
//      blob (or its caching layer's view of it); a distributed
//      implementation can run on any node.
//   5. No cross-shard ordering promises. Each (assign / revoke)
//      operation is independent.
//   6. Precision N/A — this substrate carries no temporal contract.
//
// Audit emission is part of the store contract: `AssignPlatformAdmin`
// and `RevokePlatformAdmin` record `PlatformAdminAssigned` /
// `PlatformAdminRevoked` events under `_platform` scope on success.
// The `actorUserId` argument is recorded as the audit event's `Actor`
// field — bootstrapping uses `"_bootstrap"`, API handlers pass the
// authenticated caller's userId. Callers do not emit audit themselves;
// the store guarantees it so the trail stays consistent across all
// assignment paths.

type IPlatformAdminStore =
    /// Whether `userId` currently holds `PlatformRole.PlatformAdmin`.
    /// Resolved per-request by `ScopeResolutionMiddleware` (Phase 4b
    /// commit 4c) — implementations cache as appropriate; the SDK-
    /// default re-reads the single admin-list blob on every call.
    /// Returns `false` for an empty list (a fresh deployment with no
    /// admins yet).
    abstract IsPlatformAdmin: userId: string -> Async<bool>

    /// Assign `PlatformRole.PlatformAdmin` to `targetUserId`. Records a
    /// `PlatformAdminAssigned` audit event with `Actor = actorUserId`
    /// on success. Idempotent — reassigning a user who already holds
    /// the role returns `Ok` without re-emitting the audit event.
    /// Permission checks happen at the API handler level via
    /// `canModifyPlatformConfig`; the bootstrap path passes
    /// `"_bootstrap"` as the actor, gated only by the empty-list
    /// precondition (callers verify this before invoking).
    abstract AssignPlatformAdmin: actorUserId: string * targetUserId: string -> Async<Result<unit, string>>

    /// Revoke `PlatformRole.PlatformAdmin` from `targetUserId`. Records
    /// a `PlatformAdminRevoked` audit event with `Actor = actorUserId`
    /// on success. Refuses to remove the last remaining admin — the
    /// deployment must always have at least one to avoid lockout;
    /// returns `Error "cannot revoke the last remaining Platform Admin"`
    /// in that case. Idempotent — revoking a user who doesn't hold the
    /// role returns `Ok` without emitting an audit event.
    abstract RevokePlatformAdmin: actorUserId: string * targetUserId: string -> Async<Result<unit, string>>

    /// List every user holding `PlatformRole.PlatformAdmin`. Visible to
    /// all authenticated users via `PlatformAdminApi.ListPlatformAdmins`
    /// — opaque list of userIds, no other PII attached. Used by the
    /// `PlatformAdminUI` Admins tab to show the current admin set
    /// alongside the Assign / Revoke controls.
    abstract ListPlatformAdmins: unit -> Async<string list>
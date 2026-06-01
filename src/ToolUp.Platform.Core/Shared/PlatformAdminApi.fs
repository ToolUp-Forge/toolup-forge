// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── PlatformAdminApi (Fable.Remoting wire surface) ──────────────────
//
// Deployment-wide admin role management. Client-callable API
// for the built-in `PlatformAdminUI` admin module: query the caller's
// own role, list every Platform Admin, assign / revoke the role.
//
// **Scope.** This contract covers admin role management only. The
// Platform Knowledge Base write surface (the other half of "what
// Platform Admins can do") lives in a sibling Fable.Remoting record
// shipped from the `ToolUp.KnowledgeBase` companion. Putting KB methods
// on this record would force `ToolUp.Platform.Core` to depend on
// `ToolUp.KnowledgeBase.Core` (`KnowledgeDocument` is defined there) —
// wrong-direction layering. The split keeps Core KB-free; deployments
// without KB still see the role-management surface, which is a
// precondition for the encryption-key destroy endpoint and any future
// deployment-wide admin operation.
//
// **Permission gating.** `IsPlatformAdmin` and `ListPlatformAdmins` are
// available to any authenticated caller — the former queries the
// caller's own role from `AccessContext.PlatformRole` (no store call
// needed), the latter returns an opaque list of userIds (no PII other
// than the userIds themselves, which authenticated members of the same
// deployment would learn anyway via team rosters). `AssignPlatformAdmin`
// and `RevokePlatformAdmin` are gated server-side on
// `canModifyPlatformConfig`. Failures return
// `Error "platform admin role required"` so the client can surface a
// uniform error banner.
//
// **Bootstrap path.** The very first Platform Admin is seeded by the
// SDK's bootstrap (`TOOLUP_INITIAL_PLATFORM_ADMIN` env var, fired once
// from `compose` against an empty admin list). Subsequent admins are
// assigned through `AssignPlatformAdmin` by an existing admin. Wipe +
// re-bootstrap is the documented disaster-recovery path; this API
// never offers a "force-promote without an existing admin" escape
// hatch.

type PlatformAdminApi = {
    /// Whether the caller currently holds `PlatformRole.PlatformAdmin`.
    /// Resolves the answer from `AccessContext.PlatformRole` — no
    /// `IPlatformAdminStore` call. Visible to every authenticated
    /// caller; anonymous callers always receive `false`.
    [<AllowAnonymous>]
    IsPlatformAdmin: unit -> Async<bool>

    /// List every user holding `PlatformRole.PlatformAdmin`. Visible to
    /// every authenticated caller. Returns an opaque list of userIds —
    /// no other PII attached. Used by `PlatformAdminUI` to render the
    /// current admin set alongside the Assign / Revoke controls.
    [<AllowAnonymous>]
    ListPlatformAdmins: unit -> Async<string list>

    /// Assign `PlatformRole.PlatformAdmin` to `targetUserId`. Gated on
    /// `canModifyPlatformConfig` server-side; non-admin callers receive
    /// `Error "platform admin role required"`. Idempotent — assigning
    /// to a user who already holds the role returns `Ok` without
    /// re-emitting the audit event. The actor recorded on the audit
    /// event is the calling user's `AccessContext.UserId`.
    [<RequiresRole "PlatformAdmin">]
    AssignPlatformAdmin: string -> Async<Result<unit, string>>

    /// Revoke `PlatformRole.PlatformAdmin` from `targetUserId`. Gated
    /// on `canModifyPlatformConfig`. Refuses to remove the last
    /// remaining admin (lockout protection — surfaced to the client as
    /// `Error "cannot revoke the last remaining Platform Admin"`).
    /// Idempotent — revoking a non-admin returns `Ok` without an audit
    /// event.
    [<RequiresRole "PlatformAdmin">]
    RevokePlatformAdmin: string -> Async<Result<unit, string>>

    /// Read the current runtime
    /// `PlatformKnowledgeBase` mode. Visible to every authenticated
    /// caller (the toggle state itself isn't sensitive — knowing
    /// "Platform KB is enabled" doesn't grant access to anything; the
    /// `ListPlatformDocuments` / retrieval gate enforces actual
    /// content access). Used by the Platform Admin module's Settings
    /// tab to render the current state.
    [<AllowAnonymous>]
    GetPlatformKnowledgeBase: unit -> Async<PlatformKnowledgeBaseMode>

    /// Set the runtime
    /// `PlatformKnowledgeBase` mode. Gated on `canModifyPlatformConfig`.
    /// Persists the override and updates the in-memory cell so the
    /// next retrieval call sees the new value. Survives restarts.
    /// Returns `Error` on persistence failure (the in-memory cell is
    /// only updated on successful save).
    [<RequiresRole "PlatformAdmin">]
    SetPlatformKnowledgeBase: PlatformKnowledgeBaseMode -> Async<Result<unit, string>>
}
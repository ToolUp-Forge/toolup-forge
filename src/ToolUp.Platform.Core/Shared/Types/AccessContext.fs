// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Per-request access context, resolved from the authenticated user (or
/// share-token claim, for `ClaimBearer` subjects) and the platform's
/// permission configuration. Permissions are SDK-owned, not derived
/// from auth provider JWT claims.
///
/// Phase 66 Stream A.1 — `Mode: PlatformMode` retired in favour of
/// `Subject: Subject`. The Subject is the load-bearing pivot: storage
/// scope, persistence, permissions, and audit attribution all derive
/// from it per-request, replacing the deployment-wide Mode decision
/// with a per-request one.
type AccessContext = {
    /// Stable identity string. For `AnonymousSession`, the session id;
    /// for `AuthenticatedUser` / `TeamMember`, the user id; for
    /// `ClaimBearer`, the claim's `AttributedHandle` if set, else the
    /// issuer's id (kept stable across the claim's lifetime per design
    /// §3.0 OQ2 resolution).
    UserId: string
    /// Active team, when the subject is in a team scope (`TeamMember`).
    /// `None` for every other Subject kind.
    TeamId: string option
    /// Per-request resolved subject. Replaces the old `Mode` field.
    /// Handlers that need to branch on subject kind should pattern-match
    /// on `Subject` directly or call `AccessContext.kindLabel` /
    /// `inTeamScope` / `isAnonymous` / `claim` per design §2.7.
    Subject: Subject
    /// Module-level permissions. **Empty map = unrestricted** — the
    /// RBAC feature is opt-in per deployment / team; teams that
    /// haven't configured permissions let every member access every
    /// module (the default, pre-RBAC behaviour). When populated, keys are
    /// module names and values are permission lists (e.g. `[Read]`,
    /// `[Read; Write]`, `[Admin]`).
    ModulePermissions: Map<string, ModulePermission list>
    /// Per-module exposure state for the active team (the per-team
    /// **exposure** axis — see `ModuleExposure`). Loaded for `TeamMember`
    /// subjects from the team's `TeamPermissions.Exposure`; empty for
    /// every other subject kind (nothing hidden when there is no team).
    /// A module absent from the map is `Available`. A visibility/
    /// availability concern only — `canAccessModule` / `hasPermission`
    /// remain the per-route authorization boundary. See
    /// `isModuleExposed` (sidebar/Home) and `isModuleAvailable`
    /// (data mapping).
    ModuleExposure: Map<string, ModuleExposure>
    /// Platform-wide administrative role, resolved per-request from
    /// `IPlatformAdminStore`. `None` for non-admin users —
    /// this is the default and preserves backward compatibility for
    /// every existing handler that doesn't check the field. `Some
    /// PlatformAdmin` unlocks deployment-wide admin operations gated
    /// by `canModifyPlatformConfig`.
    PlatformRole: PlatformRole option
}

module AccessContext =
    let private deriveUserId (subject: Subject) =
        match subject with
        | AnonymousSession sessionId -> sessionId
        | AuthenticatedUser userId -> userId
        | TeamMember(userId, _) -> userId
        | ClaimBearer claim ->
            // §3.0 OQ2 — AttributedHandle when set, else IssuedBy.
            // Synthetic "claim:<tokenId>" form is reserved for later
            // hardening if leaking IssuedBy proves problematic.
            match claim.AttributedHandle with
            | Some handle -> handle
            | None -> claim.IssuedBy

    let private deriveTeamId (subject: Subject) =
        match subject with
        | TeamMember(_, teamId) -> Some teamId
        | _ -> None

    /// Construct an unrestricted access context (all modules accessible)
    /// from a resolved `Subject`. Used as the fallback when no team-scoped
    /// permission config is present, and in tests. `PlatformRole` defaults
    /// to `None` — callers that need an admin context construct it directly
    /// or via a test helper.
    ///
    /// `UserId` and `TeamId` are derived from the Subject per the field
    /// doc-comments on `AccessContext`.
    let unrestricted (subject: Subject) : AccessContext = {
        UserId = deriveUserId subject
        TeamId = deriveTeamId subject
        Subject = subject
        ModulePermissions = Map.empty
        ModuleExposure = Map.empty
        PlatformRole = None
    }

    /// True when the subject is `AnonymousSession`. Convenience for
    /// migration of old `match ctx.Mode with | Anonymous -> ...` code.
    let isAnonymous (ctx: AccessContext) =
        match ctx.Subject with
        | AnonymousSession _ -> true
        | _ -> false

    /// True when the subject is anything other than `AnonymousSession`
    /// (`AuthenticatedUser`, `TeamMember`, `ClaimBearer`). Convenience
    /// for the negative branch.
    let isAuthenticated (ctx: AccessContext) = not (isAnonymous ctx)

    /// True when the subject is `TeamMember`. Use for handlers that
    /// need to gate team-scoped behaviour (team-CRUD, team-config writes).
    let inTeamScope (ctx: AccessContext) =
        match ctx.Subject with
        | TeamMember _ -> true
        | _ -> false

    /// True when the subject is `ClaimBearer`. Convenience for handlers
    /// that need to dispatch on claim-bearer paths.
    let isClaimBearer (ctx: AccessContext) =
        match ctx.Subject with
        | ClaimBearer _ -> true
        | _ -> false

    /// Returns the share-token claim if the subject is a `ClaimBearer`.
    /// Use this in handlers that need to inspect claim-bounded authority
    /// (`ResourceKind` / `ResourceId` / `UseLimit`).
    let claim (ctx: AccessContext) =
        match ctx.Subject with
        | ClaimBearer c -> Some c
        | _ -> None

    /// Stable kind label suitable for log / metrics tagging. The four
    /// values — `"anonymous"`, `"user"`, `"team"`, `"claim-bearer"` —
    /// replace the old `string ctx.Mode` form (Phase 66 §3.6 — metrics
    /// tags carry `subject_kind` rather than `mode`).
    let kindLabel (ctx: AccessContext) =
        match Subject.kind ctx.Subject with
        | AnonymousKind -> "anonymous"
        | UserKind -> "user"
        | TeamMemberKind -> "team"
        | ClaimBearerKind -> "claim-bearer"

    /// The `ModuleExposure` state of a module for the active team.
    /// A module absent from `ModuleExposure` is `Available` (the
    /// default), so this is `Available` everywhere RBAC exposure does
    /// not apply (`ModuleExposure` is empty for non-team subjects).
    let exposureOf (moduleName: string) (ctx: AccessContext) =
        ctx.ModuleExposure
        |> Map.tryFind moduleName
        |> Option.defaultValue ModuleExposure.Available

    /// Whether a module is **exposed** in the active team's sidebar +
    /// Home. Only `Available` is exposed; `Hidden` and `Unavailable` are
    /// both removed from navigation. Independent of permission — this
    /// answers "should the module be offered at all", not "may the
    /// caller use it".
    let isModuleExposed (moduleName: string) (ctx: AccessContext) =
        exposureOf moduleName ctx |> ModuleExposure.isExposed

    /// Whether a module's data types may be **mapped/detected** for the
    /// active team. `Available` and `Hidden` are both mappable; only
    /// `Unavailable` (the clearance state) blocks data mapping. Gates
    /// the Import & Map detection/processing path; the per-route
    /// permission guard remains separate.
    let isModuleAvailable (moduleName: string) (ctx: AccessContext) =
        exposureOf moduleName ctx |> ModuleExposure.isMappable

    /// Check whether the context grants any access to a given module.
    /// Empty `ModulePermissions` means unrestricted — every module is
    /// accessible. When populated, the module must be a key in the
    /// map with at least one permission. This is the **permission**
    /// axis; sidebar visibility additionally requires `isModuleExposed`.
    let canAccessModule (moduleName: string) (ctx: AccessContext) =
        ctx.ModulePermissions.IsEmpty
        || (ctx.ModulePermissions
            |> Map.tryFind moduleName
            |> Option.map (fun perms -> not (List.isEmpty perms))
            |> Option.defaultValue false)

    /// Check whether the context grants a specific permission on a
    /// module, honouring the Read / Write / Admin hierarchy: `Admin`
    /// satisfies any requirement; `Write` satisfies `Read` or `Write`;
    /// `Read` satisfies only `Read`. Empty map is unrestricted.
    let hasPermission (moduleName: string) (required: ModulePermission) (ctx: AccessContext) =
        ctx.ModulePermissions.IsEmpty
        || (ctx.ModulePermissions
            |> Map.tryFind moduleName
            |> Option.map (List.exists (fun granted -> ModulePermission.implies granted required))
            |> Option.defaultValue false)

    /// Resolve the `StorageScope` for persistent per-scope configuration
    /// (AI settings, team profiles, notification preferences, and other
    /// features that roll up the same way). Platform default:
    /// - `TeamMember`: team scope exclusively — no personal overrides.
    /// - `AuthenticatedUser`: user scope.
    /// - `ClaimBearer`: the claim's resolved scope (matches today's
    ///   `BlobShareTokenStore` semantics — `claim.ScopeId` is both the
    ///   id and the container path).
    /// - `AnonymousSession`: no persistent config → `None`.
    ///
    /// Features whose semantics genuinely differ (e.g. per-user override
    /// inside Team mode) compose their own scope resolution instead of
    /// calling this helper.
    let configScope (ctx: AccessContext) : StorageScope option =
        match ctx.Subject with
        | TeamMember(_, teamId) ->
            Some {
                ScopeId = teamId
                Container = $"team-{teamId}"
                Persist = true
            }
        | AuthenticatedUser userId ->
            Some {
                ScopeId = userId
                Container = $"user-{userId}"
                Persist = true
            }
        | ClaimBearer claim ->
            Some {
                ScopeId = claim.ScopeId
                Container = claim.ScopeId
                Persist = true
            }
        | AnonymousSession _ -> None

    /// Resolve the `FlagScope` the caller writes feature-flag overrides
    /// at. Mirrors `configScope` but targets the flag store's DU-typed
    /// scope (rather than a blob `StorageScope`):
    /// - `TeamMember`: team scope (Owner/Admin write gate enforced by the handler).
    /// - `AuthenticatedUser`: user scope.
    /// - `ClaimBearer`: no flag scope — the claim's authority envelope
    ///   is the substitute for flag-driven personalisation.
    /// - `AnonymousSession`: no persistent scope → `None`.
    ///
    /// Platform-scope writes are not exposed through this helper — they
    /// require a deployment-level admin concept that does not exist yet.
    /// The evaluator still walks `Platform` at read time so flags
    /// seeded via configuration keep working.
    let flagScope (ctx: AccessContext) : FlagScope option =
        match ctx.Subject with
        | TeamMember(_, teamId) -> Some(FlagScope.Team teamId)
        | AuthenticatedUser userId -> Some(FlagScope.User userId)
        | ClaimBearer _
        | AnonymousSession _ -> None

    /// Whether the context grants platform-wide administrative authority.
    /// Returns `true` only when `PlatformRole = Some PlatformAdmin`. Used
    /// by `PlatformAdminApiHandler` to gate every Platform
    /// Knowledge Base write, every runtime-config mutation, and the
    /// encryption-key destruction endpoint.
    ///
    /// HARD RULE PRECONDITION: this predicate is the structural enforcement
    /// point for "only Platform Admins write to `VectorScope.Platform`".
    /// Any new write path that targets Platform scope (or any other
    /// deployment-wide mutation) must guard on this predicate. Team-scope
    /// handlers (KB upload, narrative-commit, note add/update, AI-context
    /// write) derive their target from the request's `StorageScope` and
    /// never accept a caller-supplied scope, so they need no gate — but a
    /// NEW handler that accepts a target-scope parameter MUST gate on this.
    let canModifyPlatformConfig (ctx: AccessContext) : bool =
        ctx.PlatformRole = Some PlatformRole.PlatformAdmin
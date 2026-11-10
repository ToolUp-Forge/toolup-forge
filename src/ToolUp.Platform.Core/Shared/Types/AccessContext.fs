// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Phase 1107 — an in-platform grant of one publication reader role: the
/// holder may read the `Publication` pages of `PublishingScope` whose
/// audience declares `Role` among its readers, exactly as if its identity
/// provider had asserted that role — and nothing else. A grant names one
/// scope and one role; there is no wildcard, and it is never implied by
/// team membership, module permissions or the platform-admin role.
type PublicationReaderGrant = {
    /// The publishing scope (team id) whose publications the grant reads.
    PublishingScope: string
    /// The reader role granted — one the scope's publication audiences declare.
    Role: string
}

/// A principal's module permissions, as a closed type. The empty
/// map used to mean two things — "this team never configured permissions"
/// and "nothing was resolved" — and both read as every module. Now the
/// first is stated (`Unrestricted`) and the second cannot be: `Restricted
/// Map.empty` grants NOTHING.
[<RequireQualifiedAccess>]
type ModulePermissions =
    /// No module RBAC applies: the team never configured permissions (the
    /// pre-RBAC default, decided by the permission store), the subject is
    /// in no team, or the work is a named system context. Every module is
    /// accessible.
    | Unrestricted
    /// Module RBAC applies: keys are module names, values the permissions
    /// held on each (`[Read]`, `[Read; Write]`, `[Admin]`). A module absent
    /// from the map, or mapped to an empty list, is not accessible.
    | Restricted of Map<string, ModulePermission list>

module ModulePermissions =
    /// The value that grants nothing.
    let none = ModulePermissions.Restricted Map.empty

    /// The permissions held on `moduleName`: `None` under `Unrestricted`
    /// (no list applies — every permission is held) and for a module the
    /// restricted map does not name.
    let tryFind (moduleName: string) (permissions: ModulePermissions) : ModulePermission list option =
        match permissions with
        | ModulePermissions.Unrestricted -> None
        | ModulePermissions.Restricted map -> Map.tryFind moduleName map

    /// Whether `moduleName` is accessible at all.
    let grantsAccess (moduleName: string) (permissions: ModulePermissions) : bool =
        match permissions with
        | ModulePermissions.Unrestricted -> true
        | ModulePermissions.Restricted map ->
            map
            |> Map.tryFind moduleName
            |> Option.map (fun perms -> not (List.isEmpty perms))
            |> Option.defaultValue false

    /// Whether `required` is held on `moduleName`, honouring the Read / Write /
    /// Admin hierarchy (`ModulePermission.implies`).
    let grants (moduleName: string) (required: ModulePermission) (permissions: ModulePermissions) : bool =
        match permissions with
        | ModulePermissions.Unrestricted -> true
        | ModulePermissions.Restricted map ->
            map
            |> Map.tryFind moduleName
            |> Option.map (List.exists (fun granted -> ModulePermission.implies granted required))
            |> Option.defaultValue false

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
    /// Module-level permissions. a closed type: `Unrestricted`
    /// is stated where module RBAC does not apply (a team that never
    /// configured permissions — GP 11 — or a subject in no team), and
    /// `Restricted` carries the map, where an absent module is not
    /// accessible and `Restricted Map.empty` grants nothing.
    ModulePermissions: ModulePermissions
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
    /// Phase 987 — roles the identity provider asserted for this principal
    /// (`AuthenticatedUser.pageRoles`: its API roles plus its directory
    /// roles — for the OIDC provider, the token's role and group claims as
    /// `AuthConfig.ClaimMapping` maps them). Phase 993 — a directory role is
    /// carried here WITHOUT being an API role: `[<RequiresRole>]` reads
    /// `AuthenticatedUser.Roles`, which a directory role reaches only
    /// through `ClaimMapping.ApiRoleGrants`. Carried
    /// for `AuthenticatedUser` and `TeamMember` subjects only; empty for an
    /// anonymous session or a share-token bearer, and empty when no
    /// mapping is configured. Read by audience-gated pages (`ScopeGated`)
    /// alongside `ModulePermissions`. Deliberately NOT read by
    /// `canAccessModule` / `hasPermission`: module RBAC stays SDK-owned, so
    /// a directory role grants page access, never module permissions.
    TokenRoles: string list
    /// Phase 996 — how the deployment admitted the principal
    /// (`AuthenticatedUser.Admission`). `Member` for every principal unless
    /// the deployment maps a publication audience and admitted this one as a
    /// `PublicationReader`, which confines it to the `Publication` pages whose
    /// readers it holds. Carried for `AuthenticatedUser` and `TeamMember`
    /// subjects only (`admissionFor`); `Member` for every other subject.
    Admission: ToolUp.Platform.Auth.PrincipalAdmission
    /// Phase 1107 — the in-platform publication reader grants this
    /// principal holds (`PublicationReaderGrant`), read per request so a
    /// revocation takes effect at the next one. Read ONLY by the
    /// `Publication` page audience, which admits on a provider-asserted
    /// reader role OR a grant of that role for the page's publishing
    /// scope. Carried for signed-in human principals only
    /// (`publicationGrantsFor`); empty for every other subject, and empty
    /// wherever no grant store is composed.
    PublicationGrants: PublicationReaderGrant list
}

module AccessContext =
    /// The stable identity string of a subject, by the rule the
    /// `UserId` field documents: a session id, a user id, a claim's
    /// attributed handle (else its issuer). The one derivation: every
    /// context builder and every attribution reads it.
    let principalId (subject: Subject) : string =
        match subject with
        | AnonymousSession sessionId -> sessionId
        | AuthenticatedUser userId -> userId
        | TeamMember(userId, _) -> userId
        | PublicationReader userId -> userId
        | ClaimBearer claim ->
            // §3.0 OQ2 — AttributedHandle when set, else IssuedBy.
            match claim.AttributedHandle with
            | Some handle -> handle
            | None -> claim.IssuedBy

    let private teamOf (subject: Subject) =
        match subject with
        | TeamMember(_, teamId) -> Some teamId
        | _ -> None

    /// A context for `subject` holding `permissions`, and no
    /// exposure override, platform role, provider roles or grants. The one
    /// constructor: `UserId` and `TeamId` are derived from the subject.
    let ofSubject (permissions: ModulePermissions) (subject: Subject) : AccessContext = {
        UserId = principalId subject
        TeamId = teamOf subject
        Subject = subject
        ModulePermissions = permissions
        ModuleExposure = Map.empty
        PlatformRole = None
        TokenRoles = []
        Admission = ToolUp.Platform.Auth.PrincipalAdmission.Member
        PublicationGrants = []
    }

    /// The context of work that runs outside any request and
    /// carries the scope it was started with (a scheduled job, a narrative
    /// run, a background planner): `subject` is that scope's principal, and
    /// module RBAC does not apply to it — it carries the scope's identity,
    /// never a user's module authority. `reason` names the work at the call
    /// site; the subject is what the audit trail attributes.
    let carrying (reason: string) (subject: Subject) : AccessContext =
        ignore reason
        ofSubject ModulePermissions.Unrestricted subject

    /// The context of platform work that serves no principal at
    /// all (a static export, a document enumeration, an evaluation harness).
    /// The subject is an anonymous session named `system:<reason>`, so every
    /// audit row the work writes attributes it to that reason. No scope, no
    /// role: `configScope` of an anonymous subject is `None`.
    let system (reason: string) : AccessContext =
        ofSubject ModulePermissions.Unrestricted (AnonymousSession("system:" + reason))

    /// The context a request whose principal was NOT resolved is served
    /// under: the anonymous caller (`AnonymousSession "anonymous"`, so every
    /// check that reads the caller as anonymous refuses it) holding nothing —
    /// no module (`ModulePermissions.none`), no role, no scope. It replaces
    /// the unrestricted fallback the handlers carried for tests that bypassed
    /// `ScopeResolutionMiddleware`. `site` names the handler at the call site,
    /// so a reader sees where the refusal is served; it is not stored, because
    /// a principal id other than "anonymous" would read as a signed-in caller
    /// to every check keyed on the user id.
    let unresolved (site: string) : AccessContext =
        ignore site
        ofSubject ModulePermissions.none (AnonymousSession "anonymous")

    /// The `HttpContext.Items` key `ScopeResolutionMiddleware`
    /// stamps the request's principal under, ONCE, after every per-request
    /// grant was read. A request without it has no resolved principal.
    [<Literal>]
    let ItemsKey = "ToolUp.AccessContext"

#if !FABLE_COMPILER
    /// The request's context, or `None` when no principal was
    /// resolved: the scoped `AccessContext` factory yields no value for such
    /// a request, so this is the one typed read of it.
    let tryResolve (services: System.IServiceProvider) : AccessContext option =
        match services.GetService(typeof<AccessContext>) with
        | :? AccessContext as access -> Some access
        | _ -> None
#endif

    /// Phase 987 — the `TokenRoles` a context for `subject` carries, given
    /// the roles its identity provider asserted (`AuthenticatedUser.pageRoles`
    /// — API roles and, since Phase 993, directory roles):
    /// those roles, de-duplicated, for a signed-in human principal
    /// (`AuthenticatedUser` / `TeamMember`); none for an anonymous session
    /// or a share-token bearer, whose identity no provider asserted. The
    /// one rule every context builder applies.
    /// Phase 996 — the `Admission` a context for `subject` carries, given
    /// the admission its identity provider decided: that admission for a
    /// signed-in human principal (`AuthenticatedUser` / `TeamMember`);
    /// `Member` for an anonymous session or a share-token bearer, whose
    /// identity no provider asserted (the same rule as `tokenRolesFor`).
    /// Phase 1002 — a `PublicationReader` subject is a publication reader
    /// whatever the provider admission says: the subject is the authority.
    let admissionFor
        (subject: Subject)
        (providerAdmission: ToolUp.Platform.Auth.PrincipalAdmission)
        : ToolUp.Platform.Auth.PrincipalAdmission =
        match subject with
        | Subject.PublicationReader _ -> ToolUp.Platform.Auth.PrincipalAdmission.PublicationReader
        | Subject.AuthenticatedUser _
        | Subject.TeamMember _ -> providerAdmission
        | Subject.AnonymousSession _
        | Subject.ClaimBearer _ -> ToolUp.Platform.Auth.PrincipalAdmission.Member

    /// Phase 996 — `true` when the principal was admitted as a publication
    /// reader only: it is not an app user, and only `Publication` pages whose
    /// readers it holds may serve it.
    let isPublicationReader (ctx: AccessContext) : bool =
        ctx.Admission = ToolUp.Platform.Auth.PrincipalAdmission.PublicationReader

    /// Phase 1002 — a `PublicationReader` carries its provider-asserted
    /// roles: the `Publication` page gate admits it on them.
    let tokenRolesFor (subject: Subject) (providerRoles: string list) : string list =
        match subject with
        | Subject.AuthenticatedUser _
        | Subject.TeamMember _
        | Subject.PublicationReader _ -> List.distinct providerRoles
        | Subject.AnonymousSession _
        | Subject.ClaimBearer _ -> []

    /// Phase 1107 — the `PublicationGrants` a context for `subject` carries,
    /// given the grants the store holds for its user: those grants,
    /// de-duplicated, for a signed-in human principal (`AuthenticatedUser`
    /// / `TeamMember` / `PublicationReader`); none for an anonymous session
    /// or a share-token bearer (the same rule as `tokenRolesFor`).
    let publicationGrantsFor (subject: Subject) (grants: PublicationReaderGrant list) : PublicationReaderGrant list =
        match subject with
        | Subject.AuthenticatedUser _
        | Subject.TeamMember _
        | Subject.PublicationReader _ -> List.distinct grants
        | Subject.AnonymousSession _
        | Subject.ClaimBearer _ -> []

    /// True when the subject is `AnonymousSession`. Convenience for
    /// migration of old `match ctx.Mode with | Anonymous -> ...` code.
    let isAnonymous (ctx: AccessContext) =
        match ctx.Subject with
        | AnonymousSession _ -> true
        | _ -> false

    /// True when the subject is anything other than `AnonymousSession`
    /// (`AuthenticatedUser`, `TeamMember`, `ClaimBearer`, and — Phase
    /// 1002 — `PublicationReader`, which is signed in but is not an app
    /// user: `isPublicationReader` confines it). Convenience for the
    /// negative branch.
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
        | PublicationReaderKind -> "publication-reader"

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

    /// Check whether the context grants any access to a given module:
    /// every module under `Unrestricted`; under `Restricted`, a module the
    /// map names with at least one permission (`Restricted
    /// Map.empty` grants nothing). This is the **permission** axis; sidebar
    /// visibility additionally requires `isModuleExposed`.
    ///
    /// Phase 1002 — a publication reader is never an app user, so it can
    /// access no module, whatever its permission value would say.
    let canAccessModule (moduleName: string) (ctx: AccessContext) =
        not (isPublicationReader ctx)
        && ModulePermissions.grantsAccess moduleName ctx.ModulePermissions

    /// Check whether the context grants a specific permission on a
    /// module, honouring the Read / Write / Admin hierarchy: `Admin`
    /// satisfies any requirement; `Write` satisfies `Read` or `Write`;
    /// `Read` satisfies only `Read`. `Unrestricted` holds every permission.
    /// Phase 1002 — a publication reader holds no module permission.
    let hasPermission (moduleName: string) (required: ModulePermission) (ctx: AccessContext) =
        not (isPublicationReader ctx)
        && ModulePermissions.grants moduleName required ctx.ModulePermissions

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
        | TeamMember(_, teamId) -> Some(StorageScope.ofKey true (ScopeKey.Team(TeamId teamId)))
        | AuthenticatedUser userId -> Some(StorageScope.ofKey true (ScopeKey.User(UserId userId)))
        | ClaimBearer claim -> Some(StorageScope.ofKey true (ScopeKey.Claim claim.ScopeId))
        | AnonymousSession _ -> None
        // Phase 1002 — a publication reader holds no scope of its own.
        | PublicationReader _ -> None

    /// Phase 995 — the inverse of `configScope`: the access context whose
    /// `configScope` is exactly `scope`, for work that holds a scope but no
    /// signed-in principal (a scheduled job, a background run). The
    /// container prefix carries the subject kind — `configScope` mints
    /// `team-{id}` and `user-{id}` — so the round trip is exact for both:
    /// a team scope becomes `TeamMember(memberId, scope.ScopeId)`, a user
    /// scope `AuthenticatedUser scope.ScopeId`. `memberId` names the actor
    /// working inside a team scope; nothing on the config path reads it
    /// for authority (`configScope` keys a `TeamMember` on the team id
    /// alone). Any other container — a share-token claim's scope, whose
    /// subject carries a whole claim a `StorageScope` cannot reconstruct,
    /// or a session — is `None`: guessing a subject would read a DIFFERENT
    /// scope's configuration and secrets. The context is `carrying`: it
    /// carries the scope's identity, not any user's module authority.
    let forConfigScope (memberId: string) (scope: StorageScope) : AccessContext option =
        // Phase 1060 — read through the one parser, and only where the
        // container and the id agree: a scope whose two fields name
        // different principals is no team's and no user's.
        match ScopeKey.tryParseContainer scope.Container with
        | Some(ScopeKey.Team teamId) when teamId = TeamId scope.ScopeId ->
            Some(carrying "config scope" (TeamMember(memberId, teamId.Value)))
        | Some(ScopeKey.User userId) when userId = UserId scope.ScopeId ->
            Some(carrying "config scope" (AuthenticatedUser userId.Value))
        | _ -> None

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
        | AnonymousSession _
        | PublicationReader _ -> None

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
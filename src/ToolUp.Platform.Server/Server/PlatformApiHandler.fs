module ToolUp.Platform.PlatformApiHandler

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.RemotingHelpers

// ─── Platform-level handlers (split from a single 14-method PlatformApi) ──
//
// Originally one `platformApiHandler` returning a `PlatformApi` record
// of 14 methods. Split per the Tidy-Up "Split PlatformApi as it grows"
// item into five sibling builders so each Fable.Remoting proxy has its
// own route prefix and per-concern test surface:
//
//   * `platformInfoApiHandler`   → `PlatformInfoApi`   (1 method)
//   * `teamApiHandler`           → `TeamApi`            (8 methods)
//   * `permissionApiHandler`     → `PermissionApi`      (3 methods)
//   * `accessibilityApiHandler`  → `AccessibilityApi`   (1 method)
//   * `dataCatalogApiHandler`    → `DataCatalogApi`     (1 method)
//
// Each builder lazy-resolves the DI services it needs per request, so
// per-API substrate failures cannot cascade — e.g. a deployment with
// `IPermissionStore` not registered still serves `/platformInfo` and
// `/team` cleanly. The fluent compose API auto-injects all five.

// ─── Shared audit-summary helpers (PermissionApi consumers) ─────────

/// Render a `ModulePermission list` as a stable comma-separated string
/// for the audit `Permissions` field. Empty list serialises to "" — the
/// same shape the audit payload documents as "revoked".
let private permissionToString =
    function
    | ModulePermission.Read -> "Read"
    | ModulePermission.Write -> "Write"
    | ModulePermission.Admin -> "Admin"

let private permissionsToCsv (perms: ModulePermission list) =
    perms |> List.map permissionToString |> String.concat ","

/// Audit summary for a wholesale defaults replacement. One line per
/// module: `module1=Read,Write; module2=Admin`. Stable ordering (sorted
/// by module name) so two structurally-equal defaults maps produce the
/// same audit string.
let private defaultsSummary (defaults: Map<string, ModulePermission list>) =
    defaults
    |> Map.toList
    |> List.sortBy fst
    |> List.map (fun (m, ps) -> $"{m}={permissionsToCsv ps}")
    |> String.concat "; "

// ─── Per-request DI helpers ─────────────────────────────────────────

/// `userId` is read from `HttpContext.Items` (populated by
/// `ScopeResolutionMiddleware`) to avoid a second async auth call per
/// request; falls back to `"anonymous"` if the middleware did not run
/// (e.g. in tests that bypass `compose`).
let private resolveUserId (ctx: HttpContext) =
    match ctx.Items.TryGetValue "ToolUp.UserId" with
    | true, (:? string as id) -> id
    | _ -> "anonymous"

let private resolveTeamStore (ctx: HttpContext) =
    match ctx.RequestServices.GetService(typeof<ITeamStore>) with
    | :? ITeamStore as ts -> Some ts
    | _ -> None

let private resolvePermissionStore (ctx: HttpContext) =
    match ctx.RequestServices.GetService(typeof<IPermissionStore>) with
    | :? IPermissionStore as p -> Some p
    | _ -> None

let private resolveAuditLog (ctx: HttpContext) =
    match ctx.RequestServices.GetService(typeof<IAuditLog>) with
    | :? IAuditLog as a -> Some a
    | _ -> None

/// Phase 5f — resolves `IPlatformAdminStore` for the `CreateTeam`
/// admin-gate. The store is registered unconditionally by
/// `ComposeAuth.registerPlatformAdminStore`, so `None` only happens on
/// test paths that bypass `compose`. Tests asserting the gate construct
/// the store + register it themselves.
let private resolvePlatformAdminStore (ctx: HttpContext) =
    match ctx.RequestServices.GetService(typeof<IPlatformAdminStore>) with
    | :? IPlatformAdminStore as s -> Some s
    | _ -> None

/// Fire-and-forget audit emission. Phase 9 — audit events for team
/// CRUD / permission changes are emitted from the handler (not from
/// the store) because the actor's `userId` lives in the request
/// context. Emission failures never cascade — `IAuditLog.Record`
/// swallows its own writes so audit gaps don't fail primary state
/// changes.
let private recordAudit (auditLog: IAuditLog option) (scopeId: string) (audit: AuditEvent) =
    match auditLog with
    | Some a -> a.Record(scopeId, audit) |> Async.Start
    | None -> ()

// ─── PlatformInfoApi — mode + auth posture (1 method) ───────────────

/// Build the `PlatformInfoApi` handler. Stateless; only reads
/// `ServerConfig`. Always-on; no auth gating because the client shell
/// needs this before it can decide whether to render a login
/// affordance.
let platformInfoApiHandler (config: ServerConfig) =
    makeApi (fun (_: HttpContext) -> {
        GetPlatformInfo =
            fun () -> async {
                return {
                    RequiresAuth = DeploymentConfig.requiresAnyAuth config
                }
            }
    })

// ─── TeamApi — team CRUD + membership (8 methods) ───────────────────

/// Build the `TeamApi` record for one request. Resolves `ITeamStore`
/// per request; returns `Error "Team management not available in this
/// mode"` when the store is unregistered (Anonymous / Individual
/// modes). Owner/Admin gating happens inside the methods that mutate
/// membership.
///
/// Phase 5f — `CreateTeam` consults `ServerConfig.TeamCreationPolicy`:
/// under the secure-by-default `PlatformAdminOnly`, non-admin callers
/// receive `Error "Team creation requires Platform Admin"` and the
/// refusal is recorded as `TeamCreationDenied` under the `_platform`
/// scope (deployment-wide refusal trail, mirroring `PlatformAdminAssigned`
/// placement). The admin lookup short-circuits before any team id is
/// minted so the deny path is observably free of side effects.
///
/// Extracted as a separate function (rather than inlined into
/// `teamApiHandler` below the way the other API records still are)
/// so tests can construct it directly off a `DefaultHttpContext`
/// without going through Fable.Remoting's HTTP machinery — same
/// shape as `PlatformAdminApiHandler.platformAdminApi`.
let teamApi (config: ServerConfig) (ctx: HttpContext) : TeamApi =
    let teamStore = resolveTeamStore ctx
    let auditLog = resolveAuditLog ctx
    let userId = resolveUserId ctx
    let audit = recordAudit auditLog

    // Phase 5f — resolve the admin-gate at request time (the store is
    // a singleton; resolving inside the helper matches the pattern
    // used for `teamStore` / `auditLog` above so test paths that
    // bypass `compose` can construct an `HttpContext` with only the
    // services they need).
    let isAdminAllowed () = async {
        match config.TeamCreationPolicy with
        | AnyAuthenticatedUser -> return true
        | PlatformAdminOnly ->
            match resolvePlatformAdminStore ctx with
            | Some store -> return! store.IsPlatformAdmin userId
            | None ->
                // Fail closed when the policy is `PlatformAdminOnly`
                // but the store is missing. This branch is unreachable
                // in production (`compose` registers the store
                // unconditionally) and reachable only in tests that
                // bypass `compose` without wiring the store; failing
                // closed surfaces the misconfigured fixture instead of
                // silently letting the secure-by-default gate become
                // open-by-accident.
                return false
    }

    {
        CreateTeam =
            fun name -> async {
                match teamStore with
                | Some ts ->
                    let! allowed = isAdminAllowed ()

                    if not allowed then
                        audit
                            "_platform"
                            (TeamCreationDenied {
                                UserId = userId
                                AttemptedName = name
                            })

                        return Error "Team creation requires Platform Admin"
                    else
                        let teamId = Guid.NewGuid().ToString("N")[..7]
                        let! result = ts.CreateTeam(teamId, name)

                        match result with
                        | Ok team ->
                            audit
                                teamId
                                (TeamCreated {
                                    UserId = userId
                                    TeamId = teamId
                                    TeamName = name
                                })

                            let! addResult = ts.AddMember(teamId, userId, Owner)

                            match addResult with
                            | Ok() ->
                                audit
                                    teamId
                                    (MemberAdded {
                                        UserId = userId
                                        TeamId = teamId
                                        AffectedUserId = userId
                                        Role = TeamRoles.displayName Owner
                                    })
                            | Error _ -> ()

                            let! _ = ts.SetActiveTeam(userId, teamId)
                            return Ok team
                        | Error e -> return Error e
                | None -> return Error "Team management not available in this mode"
            }
        GetMyTeams =
            fun () -> async {
                match teamStore with
                | Some ts -> return! ts.GetTeamsForUser(userId)
                | None -> return []
            }
        AddTeamMember =
            fun (teamId, memberId, role) -> async {
                match teamStore with
                | Some ts ->
                    let! callerRole = ts.GetMemberRole(teamId, userId)

                    match callerRole with
                    | Some r when TeamRoles.canManageMembers r ->
                        let! result = ts.AddMember(teamId, memberId, role)

                        match result with
                        | Ok() ->
                            audit
                                teamId
                                (MemberAdded {
                                    UserId = userId
                                    TeamId = teamId
                                    AffectedUserId = memberId
                                    Role = TeamRoles.displayName role
                                })
                        | Error _ -> ()

                        return result
                    | _ -> return Error "Insufficient permissions"
                | None -> return Error "Team management not available in this mode"
            }
        ChangeMemberRole =
            fun (teamId, memberId, newRole) -> async {
                match teamStore with
                | Some ts ->
                    let! callerRole = ts.GetMemberRole(teamId, userId)

                    match callerRole with
                    | Some r when TeamRoles.canManageMembers r ->
                        // Read the affected user's prior role before
                        // mutating so the audit event can record
                        // OldRole → NewRole. The store is idempotent
                        // on no-op, so we suppress the audit when the
                        // role didn't actually change.
                        let! oldRoleOpt = ts.GetMemberRole(teamId, memberId)
                        let! result = ts.ChangeMemberRole(teamId, memberId, newRole)

                        match result, oldRoleOpt with
                        | Ok(), Some oldRole when oldRole <> newRole ->
                            audit
                                teamId
                                (MemberRoleChanged {
                                    UserId = userId
                                    TeamId = teamId
                                    AffectedUserId = memberId
                                    OldRole = TeamRoles.displayName oldRole
                                    NewRole = TeamRoles.displayName newRole
                                })
                        | _ -> ()

                        return result
                    | _ -> return Error "Insufficient permissions"
                | None -> return Error "Team management not available in this mode"
            }
        RemoveTeamMember =
            fun (teamId, memberId) -> async {
                match teamStore with
                | Some ts ->
                    let! callerRole = ts.GetMemberRole(teamId, userId)

                    match callerRole with
                    | Some r when TeamRoles.canManageMembers r ->
                        // Cache invalidation is event-driven:
                        // `TeamStore.RemoveMember` publishes
                        // `MembershipChanged` on the reserved
                        // `_platform` topic, which `TeamScopeResolver`
                        // subscribes to. Per-request membership re-check
                        // remains the defense-in-depth braces.
                        let! result = ts.RemoveMember(teamId, memberId)

                        match result with
                        | Ok() ->
                            audit
                                teamId
                                (MemberRemoved {
                                    UserId = userId
                                    TeamId = teamId
                                    AffectedUserId = memberId
                                })
                        | Error _ -> ()

                        return result
                    | _ -> return Error "Insufficient permissions"
                | None -> return Error "Team management not available in this mode"
            }
        GetTeamMembers =
            fun teamId -> async {
                match teamStore with
                | Some ts ->
                    // Leak guard: only team members can enumerate
                    // their own team's members. Non-members see an
                    // empty list (not an error — same observable
                    // shape for "no such team" and "not my team",
                    // which avoids leaking team existence).
                    let! callerRole = ts.GetMemberRole(teamId, userId)

                    match callerRole with
                    | Some _ -> return! ts.GetTeamMembers(teamId)
                    | None -> return []
                | None -> return []
            }
        SetActiveTeam =
            fun teamId -> async {
                match teamStore with
                | Some ts ->
                    // Verify caller is a member of the target team
                    // before persisting. Without this check a user
                    // could point their active-team at any team id
                    // they know; `TeamScopeResolver`'s per-request
                    // membership check would deny the scope anyway,
                    // but the stored pointer would be junk. Reject
                    // eagerly — cleaner error path, no stale state.
                    let! callerRole = ts.GetMemberRole(teamId, userId)

                    match callerRole with
                    | Some _ -> return! ts.SetActiveTeam(userId, teamId)
                    | None -> return Error "You are not a member of this team"
                | None -> return Error "Team management not available in this mode"
            }
        GetActiveTeam =
            fun () -> async {
                match teamStore with
                | Some ts -> return! ts.GetActiveTeam(userId)
                | None -> return None
            }
        GetTeamCreationPolicy = fun () -> async { return config.TeamCreationPolicy }
    }

/// Fable.Remoting route handler wrapping `teamApi`. Route mount path is
/// derived from the `TeamApi` record's namespace by ToolUp.Remoting.
let teamApiHandler (config: ServerConfig) = makeApi (teamApi config)

// ─── PermissionApi — RBAC management (3 methods, Owner/Admin only) ──

/// Build the `PermissionApi` handler. Owner/Admin only — members
/// receive `Error "Insufficient permissions"`. Resolves both
/// `ITeamStore` (for the role check) and `IPermissionStore` (for the
/// permission read/write) per request. Audit events are emitted on
/// successful writes.
let permissionApiHandler (_config: ServerConfig) =
    makeApi (fun (ctx: HttpContext) ->
        let teamStore = resolveTeamStore ctx
        let permStore = resolvePermissionStore ctx
        let auditLog = resolveAuditLog ctx
        let userId = resolveUserId ctx
        let audit = recordAudit auditLog

        {
            GetTeamPermissions =
                fun teamId -> async {
                    match teamStore, permStore with
                    | Some ts, Some ps ->
                        let! callerRole = ts.GetMemberRole(teamId, userId)

                        match callerRole with
                        | Some r when TeamRoles.canManageMembers r ->
                            let! perms = ps.GetTeamPermissions teamId
                            return Ok perms
                        | _ -> return Error "Insufficient permissions"
                    | _ -> return Error "Team management not available in this mode"
                }

            SetMemberPermissions =
                fun (teamId, memberId, moduleName, permissions) -> async {
                    match teamStore, permStore with
                    | Some ts, Some ps ->
                        let! callerRole = ts.GetMemberRole(teamId, userId)

                        match callerRole with
                        | Some r when TeamRoles.canManageMembers r ->
                            let! result = ps.SetMemberPermissions(teamId, memberId, moduleName, permissions)

                            match result with
                            | Ok() ->
                                audit
                                    teamId
                                    (PermissionChanged {
                                        UserId = userId
                                        TeamId = teamId
                                        AffectedUserId = memberId
                                        ModuleName = moduleName
                                        Permissions = permissionsToCsv permissions
                                    })
                            | Error _ -> ()

                            return result
                        | _ -> return Error "Insufficient permissions"
                    | _ -> return Error "Team management not available in this mode"
                }

            SetTeamDefaults =
                fun (teamId, defaults) -> async {
                    match teamStore, permStore with
                    | Some ts, Some ps ->
                        let! callerRole = ts.GetMemberRole(teamId, userId)

                        match callerRole with
                        | Some r when TeamRoles.canManageMembers r ->
                            let! result = ps.SetTeamDefaults(teamId, defaults)

                            match result with
                            | Ok() ->
                                // Wholesale defaults replacement: one audit
                                // event per call, with `ModuleName = ""` to
                                // mark it as a defaults-map change. The
                                // payload carries a per-module summary so a
                                // reviewer can see which modules ended up
                                // with which permissions without diffing
                                // against a separate snapshot.
                                audit
                                    teamId
                                    (PermissionChanged {
                                        UserId = userId
                                        TeamId = teamId
                                        AffectedUserId = ""
                                        ModuleName = ""
                                        Permissions = defaultsSummary defaults
                                    })
                            | Error _ -> ()

                            return result
                        | _ -> return Error "Insufficient permissions"
                    | _ -> return Error "Team management not available in this mode"
                }
        })

// ─── AccessibilityApi — sidebar filter helper (1 method) ────────────

/// Phase 55 pinning seam — the pure decision the `AccessibilityApi`
/// handler folds over. Extracted so the contract pack can pin the
/// three intertwined branches (Anonymous short-circuit, team-mode
/// onboarding, default RBAC intersection) without standing up a
/// full `HttpContext` per case.
///
/// Branches in priority order:
///   1. Anonymous subject → every Managed module is Accessible. An
///      anonymous caller has no user identity, so
///      `AccessContext.canAccessModule` would otherwise return `false`
///      for every module (no user → no permissions), hiding the entire
///      sidebar. Keys off the caller's `AccessContext`, not a
///      deployment-wide mode, so a mixed-mode deployment serving both
///      anonymous and authenticated surfaces resolves each request on
///      its own subject.
///   2. `noActiveTeamInTeamMode` (team-scoped Mode with no active
///      team) → Accessible is empty. The freshly-signed-up case
///      where every team-scoped API call would fail with
///      `NoActiveTeam`; reporting non-team modules as Accessible
///      would be confusing click-then-error UX.
///   3. Default → intersect Managed with the caller's
///      `AccessContext.ModulePermissions`. Empty permissions map
///      (pre-RBAC default) is unrestricted per
///      `AccessContext.canAccessModule`.
let computeAccessibleModules
    (config: ServerConfig)
    (accessCtx: AccessContext)
    (noActiveTeamInTeamMode: bool)
    : AccessibleModulesResponse =
    let accessible =
        if AccessContext.isAnonymous accessCtx then
            config.ModuleNames
        elif noActiveTeamInTeamMode then
            []
        else
            config.ModuleNames
            |> List.filter (fun name -> AccessContext.canAccessModule name accessCtx)

    {
        Managed = config.ModuleNames
        Accessible = accessible
    }

/// Build the `AccessibilityApi` handler. Read-only; reads the
/// per-request `AccessContext` populated by `ScopeResolutionMiddleware`.
/// Used by the client shell to filter its sidebar.
let accessibilityApiHandler (config: ServerConfig) =
    makeApi (fun (ctx: HttpContext) ->
        let userId = resolveUserId ctx

        {
            GetAccessibleModules =
                fun () -> async {
                    // Read the per-request AccessContext via DI — it's
                    // already populated by ScopeResolutionMiddleware.
                    // Fall back to an unrestricted context when DI is
                    // bypassed (tests calling the handler directly).
                    let accessCtx =
                        match ctx.RequestServices.GetService(typeof<AccessContext>) with
                        | :? AccessContext as ac -> ac
                        | _ -> AccessContext.unrestricted (AnonymousSession userId)

                    // Team-mode onboarding: a freshly-signed-up user has
                    // no active team yet. Every team-scoped API call
                    // would fail with `NoActiveTeam`, so reporting
                    // non-team modules as accessible would be a confusing
                    // click-then-error UX. Return an empty `Accessible`
                    // (but still report `Managed`) — the client hides
                    // server-managed modules the caller can't access,
                    // leaving the auto-injected TeamManager entry visible
                    // until they join or create a team. Applies to both
                    // `Team` and `MultiTeam` since both share the
                    // team-scope shape.
                    let noActiveTeamInTeamMode =
                        DeploymentConfig.hasTeamScope config && accessCtx.TeamId.IsNone

                    return computeAccessibleModules config accessCtx noActiveTeamInTeamMode
                }
        })

// ─── DataCatalogApi — data-type enumeration (1 method) ──────────────

/// Build the `DataCatalogApi` handler. Resolves `IDataCatalog` from DI
/// per request so a `ComposeExtensions.ServiceConfig` can swap in a
/// richer implementation without touching this handler.
let dataCatalogApiHandler (_config: ServerConfig) =
    makeApi (fun (ctx: HttpContext) -> {
        GetDataCatalog =
            fun () -> async {
                // Phase 7a. Resolves the catalog from DI per request
                // (rather than capturing it in a closure) so a
                // `ComposeExtensions.ServiceConfig` could swap in a
                // richer implementation without touching this handler.
                let catalog = ctx.RequestServices.GetService(typeof<IDataCatalog>) :?> IDataCatalog

                let! types = catalog.ListTypes()

                let! entries =
                    types
                    |> List.map (fun info -> async {
                        let! producers = catalog.GetProducers info.Id

                        return ({ Info = info; Producers = producers }: DataManagementTypes.DataTypeCatalogEntry)
                    })
                    |> Async.Parallel

                return ({ Types = entries |> Array.toList }: DataManagementTypes.DataCatalogResponse)
            }
    })
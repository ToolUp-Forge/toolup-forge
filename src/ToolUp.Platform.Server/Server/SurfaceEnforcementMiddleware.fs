module ToolUp.Platform.SurfaceEnforcement

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── SurfaceEnforcementMiddleware — Phase 66 Stream A.5 + A.6 ────────
//
// Canonical authentication / authorisation gate for `/api/*` paths
// after Phase 66 Stream A.6 (the pipeline cut-over). Replaces the
// retired `AuthEnforcementMiddleware`. Reads the resolved `Subject`
// (stashed on `HttpContext.Items` by `ScopeResolutionMiddleware`,
// which calls `ISubjectResolver` per request) and the per-route
// `SurfaceRequirement` from the composition-time registry, then
// enforces the 7-row response-code matrix from design §3.1:
//
//   | Subject kind     | Surface admits | Response                       |
//   |------------------|----------------|--------------------------------|
//   | AnonymousKind    | No             | 401 — authentication_required  |
//   | UserKind         | Yes            | 200 — pass                     |
//   | UserKind         | No (Team only) | 403 — team_required + hint     |
//   | TeamMemberKind   | Yes            | 200 — pass                     |
//   | TeamMemberKind   | No (Anon only) | 403 — auth_subject_not_admit'd |
//   | ClaimBearerKind  | Yes            | 200 — pass                     |
//   | ClaimBearerKind  | No             | 403 — claim_bearer_not_admit'd |
//
// Route lookup is three-tier:
//   1. Exact `(method, path)` match in the registry's `Exact` map.
//   2. Else longest-prefix match against `ModulePrefixes`; use that
//      module's `DefaultSurfaceRequirement`.
//   3. Else `SurfaceRequirement.userOrTeam` (strict global default,
//      fail-closed per design §3.0 OQ6).
//
// **Path scoping.** The middleware only enforces against `/api/*`
// paths. Static assets, the SPA shell, `/dev/*` diagnostics, and
// health endpoints pass through unchanged — mirroring the retired
// `AuthEnforcementMiddleware`'s scoping. `ScopeResolutionMiddleware`
// also only stashes `Subject` for `/api/*` and `/dev/*`, so paths
// outside that set arrive here with no Subject and fall through.
//
// **Subject source.** Reads `HttpContext.Items[SubjectItemsKey]`,
// populated by `ScopeResolutionMiddleware` for every `/api/*`
// request (including the synthetic-`AnonymousSession` fallback that
// `ScopeResolutionMiddleware` stashes when `ISubjectResolver`
// returns `Error _` — keeps the matrix uniform across resolver
// failure modes). When the items entry is missing on an `/api/*`
// path the middleware falls through to `next.Invoke` defensively;
// this codepath is reachable only if a downstream consumer rewrote
// the request to add `/api` prefix after `ScopeResolutionMiddleware`
// ran, which is not a supported pipeline arrangement.

[<Literal>]
let SubjectItemsKey = "ToolUp.Subject"

/// Per-process route → `SurfaceRequirement` registry, populated at
/// composition time by `addModules` (Phase 66 Stream B.3 wiring).
/// Two layers, consulted in order:
///
///   - `Exact` — exact `(method, path)` overrides for handler-level
///     declarations (e.g. Forms public-submit endpoint declaring
///     `claimBearerOnly` on its specific path).
///   - `ModulePrefixes` — per-module defaults keyed by route prefix
///     (e.g. `/api/forms/admin/` → `userOrTeam`). Longest-prefix wins
///     on lookup. Stored as a list so insertion order doesn't affect
///     match semantics.
///
/// The registry itself is value-typed; composition wires one record
/// into DI as a singleton.
type SurfaceRequirementRegistry = {
    /// Exact-match overrides. Key: `(httpMethod, path)` with method
    /// upper-cased and path lower-cased to avoid case-sensitivity
    /// surprises across clients.
    Exact: Map<string * string, SurfaceRequirement>
    /// Per-module defaults. List of `(routePrefix, requirement)`;
    /// `routePrefix` is the module's mount path (e.g.
    /// `/api/forms/admin/`). Prefix comparison is case-insensitive
    /// `StartsWith`. Longest match wins.
    ModulePrefixes: (string * SurfaceRequirement) list
}

module SurfaceRequirementRegistry =
    /// The strict global fallback when neither an exact match nor
    /// a module prefix admits the request — `userOrTeam`. Per design
    /// §3.0 OQ6 resolution: routes that declare no requirement are
    /// fail-closed, not fail-open.
    let strictDefault: SurfaceRequirement = SurfaceRequirement.userOrTeam

    /// Empty registry — every request falls through to the strict
    /// global default. Used as the boot-time value before any module
    /// registrations land. Stream B.4 populates the SDK built-ins.
    let empty: SurfaceRequirementRegistry = {
        Exact = Map.empty
        ModulePrefixes = []
    }

    /// Phase 66 Stream B.4 — explicit per-route overrides for the
    /// SDK built-in `TeamApi`'s team-CRUD mutating endpoints. Per
    /// design §3.3 — the `PlatformApi` row's "Per-endpoint overrides:
    /// team-CRUD endpoints require `teamScoped`" caveat:
    ///
    /// * `AddTeamMember`, `RemoveTeamMember`, `ChangeMemberRole`,
    ///   `GetTeamMembers`, `SetActiveTeam` — operations that require
    ///   the caller to already be in a team scope. A `UserKind`
    ///   subject (signed-in but no active team) hitting one of these
    ///   today reaches the handler and gets an internal `"Not in
    ///   team scope"` `Error` string; with the override they fail
    ///   fast at the middleware with a clean `403 team_required` +
    ///   `select_team` hint, which the client shell can render as an
    ///   actionable "Select a team to continue" panel without
    ///   parsing handler-specific error strings.
    ///
    /// Excluded from this list (kept at the strict global default
    /// `userOrTeam`):
    /// * `CreateTeam` — caller has no team yet; `teamScoped` would
    ///   reject the bootstrap path.
    /// * `GetMyTeams` / `GetActiveTeam` / `GetTeamCreationPolicy` —
    ///   `UserKind` callers with zero teams legitimately need to
    ///   read these to render the team-management UI.
    ///
    /// The routes use Fable.Remoting's default builder
    /// (`/api/{typeName}/{methodName}`) — for the `TeamApi` record
    /// type that's `/api/TeamApi/...`. Method is always `POST` (the
    /// Fable.Remoting transport's wire convention). Registry lookup
    /// normalises both at insert and resolve time, so the declarations
    /// here win regardless of the casing the client uses.
    let private sdkTeamApiCrudOverrides: ((string * string) * SurfaceRequirement) list = [
        ("POST", "/api/TeamApi/AddTeamMember"), SurfaceRequirement.teamScoped
        ("POST", "/api/TeamApi/RemoveTeamMember"), SurfaceRequirement.teamScoped
        ("POST", "/api/TeamApi/ChangeMemberRole"), SurfaceRequirement.teamScoped
        ("POST", "/api/TeamApi/GetTeamMembers"), SurfaceRequirement.teamScoped
        ("POST", "/api/TeamApi/SetActiveTeam"), SurfaceRequirement.teamScoped
    ]

    /// Phase 66 Stream B.4 — exact-match overrides the SDK contributes
    /// to every deployment regardless of `ServerConfig` shape. Today
    /// only the `TeamApi` team-CRUD set ships here; future B.4
    /// continuations and Stream B.6 (Forms public-submit) extend the
    /// list. Surfaced publicly so test fixtures and consumers can
    /// inspect / extend the declarations without poking at the
    /// `fromServerConfig` private body.
    let sdkBuiltInRouteOverrides: ((string * string) * SurfaceRequirement) list =
        sdkTeamApiCrudOverrides

    /// Build a registry from the legacy `ServerConfig` carve-out
    /// fields (`Surfaces`, `PeerRoutePrefixes`, `SseAuthMode`). Used
    /// by `SDK.Server.fs` during the Phase 66 A.6 → B.4 transition
    /// window — Stream B.4 will populate the registry from per-module
    /// `DefaultSurfaceRequirement` declarations and supersede this
    /// bridge. The mapping mirrors the retired
    /// `AuthEnforcementMiddleware`'s carve-out list so the
    /// `SurfaceEnforcementMiddleware` matrix admits the same set of
    /// unauthenticated requests:
    ///
    /// * `/api/csrf-token` → `public_` (the token endpoint must be
    ///   reachable before any credentials exist).
    /// * `/api/ai/events`, `/api/notifications` → `public_` only
    ///   when `SseAuthMode = QueryParamFallback` (the carve-out the
    ///   retired middleware applied to the EventSource handshake).
    /// * Each entry in `config.PeerRoutePrefixes` → `public_`. The
    ///   peer-bearer middleware authenticates these requests via
    ///   its own gate; the matrix admit check is a no-op for them.
    /// * In deployments without any authenticated surface (only
    ///   `Anonymous`) the `/api/` prefix defaults to `public_` so the
    ///   strict `userOrTeam` global fallback doesn't 401 anonymous
    ///   requests before per-module declarations land. Stream B.4
    ///   retires this fallback in favour of per-module
    ///   `DefaultSurfaceRequirement` declarations.
    ///
    /// **CSRF token-path literal.** The string `"/api/csrf-token"`
    /// is duplicated here from `Server/CsrfMiddleware.fs`'s
    /// `Csrf.TokenPath` literal because this module compiles
    /// before `CsrfMiddleware.fs` in the fsproj order. The
    /// duplication is a known maintenance hazard — if the token
    /// path moves, both definitions must move in lockstep.
    let fromServerConfig (config: ServerConfig) : SurfaceRequirementRegistry =
        let toLower (s: string) =
            match s with
            | null -> ""
            | x -> x.ToLowerInvariant()

        let exactCsrf = ("GET", "/api/csrf-token"), SurfaceRequirement.public_

        let exactSse =
            match config.SseAuthMode with
            | QueryParamFallback -> [
                ("GET", "/api/ai/events"), SurfaceRequirement.public_
                ("GET", "/api/notifications"), SurfaceRequirement.public_
              ]
            | CookieRequired -> []

        let peerPrefixBridge =
            config.PeerRoutePrefixes
            |> List.map (fun p -> toLower p, SurfaceRequirement.public_)

        let surfacesBridge =
            if DeploymentConfig.requiresAnyAuth config then
                []
            else
                [ "/api/", SurfaceRequirement.public_ ]

        // Phase 66 Stream B.4 — SDK built-in per-route overrides
        // (today: TeamApi team-CRUD endpoints → `teamScoped`).
        // Normalised here so the keys land in the same shape the
        // resolver looks up against — method upper-cased, path
        // lower-cased. The bridge applies them unconditionally on
        // every deployment shape: a `UserKind` caller hitting
        // `/api/teamapi/addteammember` in any non-Anonymous-only
        // deployment shape fails fast with `403 team_required +
        // select_team` rather than reaching the handler's internal
        // "Not in team scope" error string.
        let sdkBuiltIns =
            sdkBuiltInRouteOverrides
            |> List.map (fun ((m, p), r) -> (m.ToUpperInvariant(), p.ToLowerInvariant()), r)

        let exactAll = exactCsrf :: (exactSse @ sdkBuiltIns)

        {
            Exact = Map.ofList exactAll
            ModulePrefixes = surfacesBridge @ peerPrefixBridge
        }

    /// Phase 66 Stream B.3 — overlay per-module surface-requirement
    /// declarations on top of a base registry (typically the bridge
    /// returned by `fromServerConfig`). Module declarations win on
    /// exact-match overrides; module-prefix defaults append to the
    /// existing `ModulePrefixes` list and participate in
    /// longest-prefix-wins resolution.
    ///
    /// Inputs:
    ///   - `moduleSurfaceDefaults` — `(routePrefix, default)` pairs
    ///     accumulated by `ServerApp.addModule` from each
    ///     `ServerModule.RoutePrefixes` × `DefaultSurfaceRequirement`.
    ///   - `routeSurfaceOverrides` — `((method, path), requirement)`
    ///     pairs accumulated from each `ServerModule.RouteSurfaceRequirements`.
    ///
    /// Module exact overrides supersede the base's exact entries —
    /// a module declaring `claimBearerOnly` on a specific Forms
    /// public-submit endpoint wins over the `fromServerConfig` CSRF
    /// / SSE carve-outs if they collide on the same `(method, path)`.
    /// Conversely, the base's existing `ModulePrefixes` (peer-bearer
    /// prefixes, anonymous-deployment `/api/` catch-all) are
    /// preserved — the SDK's compose-time bridges and module-author
    /// declarations coexist, ordered by prefix length at resolve time.
    let merge
        (moduleSurfaceDefaults: (string * SurfaceRequirement) list)
        (routeSurfaceOverrides: ((string * string) * SurfaceRequirement) list)
        (registry: SurfaceRequirementRegistry)
        : SurfaceRequirementRegistry =
        let normaliseExactKey (httpMethod: string, path: string) =
            httpMethod.ToUpperInvariant(), path.ToLowerInvariant()

        let mergedExact =
            routeSurfaceOverrides
            |> List.fold (fun acc (key, req) -> Map.add (normaliseExactKey key) req acc) registry.Exact

        let normalisedPrefixes =
            moduleSurfaceDefaults
            |> List.map (fun (prefix, req) ->
                (match prefix with
                 | null -> ""
                 | p -> p.ToLowerInvariant()),
                req)

        {
            Exact = mergedExact
            ModulePrefixes = registry.ModulePrefixes @ normalisedPrefixes
        }

    /// Resolve the `SurfaceRequirement` that applies to the given
    /// request. Three-tier per the module preamble. Always returns
    /// a value; the strict default is the fail-closed floor.
    let resolve (registry: SurfaceRequirementRegistry) (httpMethod: string) (path: string) : SurfaceRequirement =
        let methodNormalised = httpMethod.ToUpperInvariant()
        let pathNormalised = path.ToLowerInvariant()

        match registry.Exact.TryFind(methodNormalised, pathNormalised) with
        | Some req -> req
        | None ->
            let longestPrefix =
                registry.ModulePrefixes
                |> List.filter (fun (prefix, _) ->
                    pathNormalised.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                |> List.sortByDescending (fun (prefix, _) -> prefix.Length)
                |> List.tryHead

            match longestPrefix with
            | Some(_, req) -> req
            | None -> strictDefault

/// Outcome of evaluating a `SurfaceRequirement` against the resolved
/// `Subject`. The middleware translates these into HTTP responses;
/// also surfaced as a pure function for unit-testing the matrix
/// independent of the ASP.NET Core plumbing.
type SurfaceEnforcementOutcome =
    | Pass
    | Reject of statusCode: int * errorCode: string * hint: string option

module SurfaceEnforcement =
    /// Pure evaluator for the design §3.1 7-row response matrix.
    /// Returns `Pass` when the subject's kind is admitted by the
    /// requirement; otherwise the `Reject` carries the precise
    /// status code + machine-readable error code + optional client-
    /// actionable hint.
    let evaluate (subject: Subject) (requirement: SurfaceRequirement) : SurfaceEnforcementOutcome =
        let subjectKind = Subject.kind subject

        if requirement.AcceptedSubjects.Contains subjectKind then
            Pass
        else
            match subjectKind with
            | AnonymousKind ->
                // Row 1: anonymous request to an authentication-
                // required route. 401 so the client knows
                // "credentials would unblock this".
                Reject(401, "authentication_required", None)
            | UserKind when requirement.AcceptedSubjects.Contains TeamMemberKind ->
                // Row 3: user logged in but the route requires
                // a team. 403 + `select_team` hint lets the client
                // shell render an actionable "Select a team to
                // continue" panel rather than a generic error.
                Reject(403, "team_required", Some "select_team")
            | UserKind ->
                // Generic 403 — the deployment admits this user
                // somewhere but not on this route. No hint; the
                // user has no further action available.
                Reject(403, "user_subject_not_admitted", None)
            | TeamMemberKind when requirement.AcceptedSubjects.Contains AnonymousKind ->
                // Row 5: anonymous-only sign-up flow rejecting an
                // already-authenticated subject. 403 (the route
                // exists but is closed to this caller).
                Reject(403, "authenticated_subject_not_admitted", None)
            | TeamMemberKind -> Reject(403, "team_member_not_admitted", None)
            | ClaimBearerKind ->
                // Row 7: share-token-bearing request to a route
                // that doesn't admit claim-bearers (e.g. an admin
                // endpoint where presenting a token is a sign of
                // an attack-surface probe). 403; no hint.
                Reject(403, "claim_bearer_not_admitted", None)

let private writeRejection (ctx: HttpContext) (statusCode: int) (errorCode: string) (hint: string option) = task {
    ctx.Response.StatusCode <- statusCode
    ctx.Response.ContentType <- "application/json"

    let body =
        match hint with
        | Some h -> sprintf "{\"error\":\"%s\",\"status\":%d,\"hint\":\"%s\"}" errorCode statusCode h
        | None -> sprintf "{\"error\":\"%s\",\"status\":%d}" errorCode statusCode

    do! ctx.Response.WriteAsync body
}

/// ASP.NET Core middleware enforcing per-route `SurfaceRequirement`
/// against the resolved `Subject`. The canonical authentication /
/// authorisation gate for `/api/*` paths post Phase 66 Stream A.6;
/// the retired `AuthEnforcementMiddleware`'s replacement.
///
/// Path-scoped to `/api/*` — see module preamble for the rationale
/// and for the `Subject`-source contract.
type SurfaceEnforcementMiddleware(next: RequestDelegate, registry: SurfaceRequirementRegistry) =
    member _.InvokeAsync(ctx: HttpContext) =
        task {
            let path = ctx.Request.Path
            let isApi = path.StartsWithSegments(Microsoft.AspNetCore.Http.PathString "/api")

            if not isApi then
                // Static assets, SPA shell, /dev/*, /health, /ready,
                // /metrics — all reach handlers without a
                // SurfaceRequirement gate. Mirrors retired
                // `AuthEnforcementMiddleware`'s `/api/*`-only scope.
                do! next.Invoke(ctx)
            else
                let subjectOpt =
                    match ctx.Items.TryGetValue SubjectItemsKey with
                    | true, (:? Subject as s) -> Some s
                    | _ -> None

                match subjectOpt with
                | None ->
                    // Defensive — ScopeResolutionMiddleware stashes
                    // a `Subject` (resolved or synthetic-anonymous
                    // fallback) for every `/api/*` request. Reaching
                    // this branch means a downstream rewrite added
                    // an `/api` prefix after scope resolution, which
                    // is not a supported pipeline arrangement.
                    do! next.Invoke(ctx)
                | Some subject ->
                    let requirement =
                        SurfaceRequirementRegistry.resolve registry ctx.Request.Method ctx.Request.Path.Value

                    match SurfaceEnforcement.evaluate subject requirement with
                    | Pass -> do! next.Invoke(ctx)
                    | Reject(statusCode, errorCode, hint) -> do! writeRejection ctx statusCode errorCode hint
        }
        :> System.Threading.Tasks.Task
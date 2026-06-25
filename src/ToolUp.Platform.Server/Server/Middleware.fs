module ToolUp.Platform.Middleware

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.ShareTokenAuth
open ToolUp.Platform.SurfaceEnforcement
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Tracing

// ─── Phase 66 Stream A.6 — pipeline cut-over ─────────────────────────
//
// `ScopeResolutionMiddleware` now calls `ISubjectResolver` (the new
// resolver from Stream A.3) instead of the retiring per-mode
// `IStorageScopeResolver`. It stashes a richer set on
// `HttpContext.Items`:
//   * `"ToolUp.Subject"`      — the resolved `Subject` value (the
//                               new load-bearing pivot).
//   * `"ToolUp.StorageScope"` — derived from the Subject by the
//                               transitional `StorageScopeDerivation`
//                               helper (Phase 66 Stream A.2 collapses
//                               this when `Surfaces` lands on
//                               `ServerConfig`).
//   * `"ToolUp.User"` / `"ToolUp.UserId"` — extracted from
//                               `IAuthProvider.GetUser`, unchanged.
//   * `"ToolUp.ScopeResult"`  — legacy-shape `Result<StorageScope,
//                               ScopeResolutionError>` for handlers
//                               that have not yet migrated to the
//                               Subject model. Stream A.7 finishes
//                               the consumer migration; until then
//                               the bridge translates resolver
//                               errors back into the legacy shape.
//
// `AuthEnforcementMiddleware` retired in this commit. Its role is
// now covered by `SurfaceEnforcementMiddleware` (Stream A.5) reading
// `SurfaceRequirementRegistry` per route.

/// Build a `SubjectResolutionRequest` from a live `HttpContext`.
/// Lives in server code so the resolver interface itself stays
/// runtime-agnostic (GP 12). Mirrors the retiring
/// `ScopeRequestExtractor` shape but adds the `Claim` field, read
/// from `HttpContext.Items` (populated by
/// `ShareTokenAuthMiddleware` ahead of this middleware in the
/// pipeline).
module SubjectRequestExtractor =
    let private sessionIdFrom (ctx: HttpContext) =
        match ctx.Request.Headers.TryGetValue "X-User-Id" with
        | true, values when values.Count > 0 -> Some(string values[0])
        | _ -> None

    let private headersOf (ctx: HttpContext) =
        ctx.Request.Headers
        |> Seq.map (fun kv -> kv.Key.ToLowerInvariant(), string kv.Value)
        |> Map.ofSeq

    let private claimFrom (ctx: HttpContext) : ShareTokenClaim option =
        match ctx.Items.TryGetValue ShareTokenClaimItemsKey with
        | true, (:? ShareTokenClaim as c) -> Some c
        | _ -> None

    /// Build a `SubjectResolutionRequest` from the current
    /// `HttpContext`. Uses `IAuthProvider.GetUser` (lenient — returns
    /// anonymous on no credentials); the resolver detects anonymous
    /// users and walks the appropriate step of the four-step algorithm.
    let fromHttpContext (ctx: HttpContext) (authProvider: IAuthProvider) = async {
        // Phase 11.C.5 Tier 3 — wrap `HttpContext` into the opaque
        // `RequestContext` newtype at the single call site that crosses
        // the IAuthProvider boundary. Server-tier `ofHttpContext` is
        // the only sanctioned construction path.
        let! user = authProvider.GetUser(ToolUp.Platform.RequestContextBuilder.ofHttpContext ctx)

        return {
            User = Some user
            SessionId = sessionIdFrom ctx
            Claim = claimFrom ctx
            Headers = headersOf ctx
        }
    }

/// Derive the per-request `StorageScope` from the resolved
/// `Subject` and the deployment's legacy `PlatformMode`. The
/// `Mode`-based persistence policy is a transitional bridge —
/// Phase 66 Stream A.2 replaces this with a `SurfaceProfile`-
/// driven derivation when `ServerConfig` gains `Surfaces`.
///
/// Persistence mapping post Phase 66 Stream A.2 — derived from the
/// matching `SurfaceProfile` in `config.Surfaces`:
/// * `AnonymousSession`   → `AnonymousConfig.Persistence`. Defaults
///                          to `Ephemeral` (matches retiring
///                          `AnonymousScopeResolver`); long-lived
///                          demo surfaces opt into `Persistent` via
///                          `SurfaceProfile.anonymousPersistent`.
/// * `AuthenticatedUser`  → `AuthenticatedUserConfig.Persistence`.
///                          `Ephemeral` matches the retiring
///                          `AuthenticatedEphemeralScopeResolver`;
///                          `Persistent` matches `AuthenticatedScopeResolver`.
/// * `TeamMember`         → `TeamConfig.Persistence`. Almost always
///                          `Persistent` (matches retiring
///                          `TeamScopeResolver`).
/// * `ClaimBearer`        → `Persist = true` — claim-bearer flows
///                          land in persistent scopes carried on the
///                          claim itself (`BlobShareTokenStore`
///                          semantics).
module StorageScopeDerivation =
    /// Stable label for a subject's kind — used in the fail-closed
    /// undeclared-kind diagnostic. Not the scope id (which can carry a
    /// client-suppliable session id); just the DU shape.
    let private subjectKindLabel (subject: Subject) : string =
        match subject with
        | AnonymousSession _ -> "Anonymous"
        | Subject.AuthenticatedUser _ -> "AuthenticatedUser"
        | TeamMember _ -> "Team"
        | Subject.ClaimBearer _ -> "ClaimBearer"

    /// Resolve a subject's persistence flag from the declared
    /// `Surfaces`. Returns `(persist, defaulted)` where `defaulted` is
    /// `true` when no `SurfaceProfile` matched the subject kind.
    ///
    /// Phase 246 — the no-matching-profile fallback is now **fail-closed**
    /// (`false` / ephemeral). Previously it was `Option.defaultValue true`
    /// (persistent): a subject kind absent from a deployment's declared
    /// `Surfaces` (e.g. an `AnonymousSession` fallback in a deployment
    /// that never declared `Anonymous`) silently landed in a persistent
    /// `session-{sid}` container keyed on a client-suppliable session id —
    /// fail-open data retention under an undeclared scope. Failing closed
    /// makes the secure default the default; the `defaulted` signal lets
    /// the caller emit a one-time diagnostic so the gap stays visible.
    let private persistenceFor (surfaces: SurfaceProfile list) (subject: Subject) : bool * bool =
        let pick projector =
            match surfaces |> List.tryPick projector with
            | Some persistent -> persistent, false
            | None -> false, true // fail closed (ephemeral) + signal the default fired

        match subject with
        | AnonymousSession _ ->
            pick (function
                | SurfaceProfile.Anonymous cfg -> Some(cfg.Persistence = Persistent)
                | _ -> None)
        | Subject.AuthenticatedUser _ ->
            pick (function
                | SurfaceProfile.AuthenticatedUser cfg -> Some(cfg.Persistence = Persistent)
                | _ -> None)
        | TeamMember _ ->
            pick (function
                | SurfaceProfile.Team cfg -> Some(cfg.Persistence = Persistent)
                | _ -> None)
        // A claim carries its own persistent scope (BlobShareTokenStore
        // semantics) — it is never matched against Surfaces, so it never
        // "defaults".
        | Subject.ClaimBearer _ -> true, false

    /// Subject kinds for which the fail-closed default has already been
    /// warned this process — so the diagnostic fires once per kind, not
    /// once per request. A `ConcurrentDictionary` used as a thread-safe
    /// set; the value is irrelevant.
    let private undeclaredKindWarned =
        System.Collections.Concurrent.ConcurrentDictionary<string, bool>()

    let private warnUndeclaredKindOnce (logger: ILogger option) (subject: Subject) (config: ServerConfig) : unit =
        // Check the logger BEFORE consuming the once-per-kind slot, so a
        // logger-less call (no observability sink composed) doesn't burn
        // the slot and suppress the warning for a later logger-bearing call.
        match logger with
        | None -> ()
        | Some log ->
            let kind = subjectKindLabel subject

            if undeclaredKindWarned.TryAdd(kind, true) then
                log.Warn(
                    sprintf
                        "StorageScopeDerivation: subject kind %s is not declared in ServerConfig.Surfaces (%s), so its storage scope fails closed to EPHEMERAL (Persist = false). The resolver can still produce this kind (e.g. an anonymous fallback in a deployment that never declared Anonymous); declare the matching SurfaceProfile if this kind should persist. (Logged once per kind per process.)"
                        kind
                        (DeploymentConfig.surfacesLabel config)
                )

    /// Derive the storage scope for a subject. `logger` (best-effort)
    /// receives the one-time fail-closed-default diagnostic when the
    /// subject's kind is undeclared in `Surfaces` (Phase 246).
    let fromSubject (logger: ILogger option) (config: ServerConfig) (subject: Subject) : StorageScope =
        let persist, defaulted = persistenceFor config.Surfaces subject

        if defaulted then
            warnUndeclaredKindOnce logger subject config

        match subject with
        | AnonymousSession sid -> {
            ScopeId = sid
            Container = $"session-{sid}"
            Persist = persist
          }
        | Subject.AuthenticatedUser uid -> {
            ScopeId = uid
            Container = $"user-{uid}"
            Persist = persist
          }
        | TeamMember(_, tid) -> {
            ScopeId = tid
            Container = $"team-{tid}"
            Persist = persist
          }
        | Subject.ClaimBearer claim -> {
            ScopeId = claim.ScopeId
            Container = claim.ScopeId
            Persist = persist
          }

/// Map a `SubjectResolutionError` back into the legacy
/// `ScopeResolutionError` shape used by handlers that still read
/// `HttpContext.Items["ToolUp.ScopeResult"]`. Phase 66 Stream A.7
/// migrates the remaining handlers; until then the bridge keeps the
/// legacy wire-format working.
module SubjectResolutionErrorBridge =
    let toScopeError (err: SubjectResolutionError) : ScopeResolutionError =
        match err with
        | SubjectResolutionError.UnsupportedSubject _ -> NotAuthenticated
        | SubjectResolutionError.NotTeamMember teamId -> NotTeamMember teamId
        | SubjectResolutionError.SubjectResolutionFailed msg -> ScopeResolutionFailed msg

/// Phase 246 — observe an authorization-shaped scope-resolution
/// downgrade. When `ISubjectResolver.Resolve` returns `Error err`, the
/// middleware synthesises an anonymous fallback; pre-246 that was silent
/// (`| Error _ -> ()`), so a user who was removed from their team
/// (`NotTeamMember`) or whose subject kind the deployment's `Surfaces`
/// don't serve (`UnsupportedSubject`) produced exactly the same
/// observable shape as an ordinary anonymous request. Both are *returned*
/// `Result.Error`s — `DefaultSubjectResolver` never throws them — so the
/// Phase 234 "A1" throw-catch never sees them.
///
/// Emits a distinct structured `Warn` + best-effort audit. The audit
/// reuses the `AuthScopeResolutionFailed` event with the bridged case
/// name as `ExceptionKind`, which keeps the change inside this file
/// (no new audit-DU case → no schema-version bump) while staying
/// queryable and distinct from both a normal anonymous request (which
/// emits nothing) and an A1 infra throw (which carries a real .NET
/// exception type name).
///
/// `SubjectResolutionFailed` is **suppressed** here: the resolver already
/// logs the underlying throw before converting it, and the A1 catch
/// audits the infra-failure class — a middleware line would duplicate.
///
/// Every observability call is wrapped (a logger/audit failure on the
/// auth path must not bring the request down), mirroring A1.
///
/// The decision of *what* to emit (and the `SubjectResolutionFailed`
/// suppression) is the pure `resolverDowngradeSignal` below — `Some(case
/// label, detail)` for the two authorization-shaped outcomes, `None` to
/// suppress. Extracted so the policy is unit-testable without an
/// `HttpContext` / audit-sink harness.
let resolverDowngradeSignal (err: SubjectResolutionError) : (string * string) option =
    match err with
    | SubjectResolutionError.NotTeamMember teamId ->
        Some("NotTeamMember", sprintf "active-team pointer set to team '%s' but the user is no longer a member" teamId)
    | SubjectResolutionError.UnsupportedSubject kind ->
        Some("UnsupportedSubject", sprintf "the deployment's Surfaces admit no shape for subject kind %A" kind)
    // Already logged by the resolver + audited by A1 — do not duplicate.
    | SubjectResolutionError.SubjectResolutionFailed _ -> None

let private observeResolverErrorDowngrade
    (ctx: HttpContext)
    (logger: ILogger option)
    (err: SubjectResolutionError)
    (userId: string)
    : unit =
    match resolverDowngradeSignal err with
    | None -> ()
    | Some(caseLabel, detail) ->
        let methodName = ctx.Request.Method
        let pathStr = string ctx.Request.Path

        try
            match logger with
            | Some log ->
                log.Warn(
                    sprintf
                        "ScopeResolutionMiddleware downgraded to anonymous on %s %s — %s (user '%s': %s). This is NOT an ordinary anonymous request: the subject resolved but its scope was refused. Expected transiently while a user is mid-team-removal; a sustained rate indicates a Surfaces / membership misconfiguration."
                        methodName
                        pathStr
                        caseLabel
                        userId
                        detail
                )
            | None -> ()
        with _ ->
            ()

        try
            match ctx.RequestServices.GetService(typeof<IAuditLog>) with
            | :? IAuditLog as auditLog ->
                let correlationId = ToolUp.Remoting.Server.CallContext.correlationId ()

                auditLog.Record(
                    "_platform",
                    AuthScopeResolutionFailed {
                        Method = methodName
                        Path = pathStr
                        ExceptionKind = caseLabel
                        Message = sprintf "scope downgraded to anonymous for user '%s': %s" userId detail
                        CorrelationId = correlationId
                        OccurredAt = DateTimeOffset.UtcNow
                    }
                )
                |> Async.Start
            | _ -> ()
        with _ ->
            ()

/// ASP.NET Core middleware that resolves the per-request `Subject`,
/// `StorageScope`, and authenticated user, stashing all three on
/// `HttpContext.Items`. See the module preamble for the full key
/// inventory and the Stream A.6 cut-over notes.
///
/// Runs for `/api/*` and `/dev/*` paths only — static assets, the
/// SPA shell, and health endpoints skip the resolver. Infrastructure
/// errors are caught and translated to a synthetic-`AnonymousSession`
/// fallback so the downstream `SurfaceEnforcementMiddleware` matrix
/// can apply uniformly (the `userOrTeam` strict default rejects the
/// fallback with 401, preserving the retiring `AuthEnforcementMiddleware`
/// behaviour for auth-requiring deployments).
type ScopeResolutionMiddleware(next: RequestDelegate, config: ServerConfig) =
    // `StartImmediateAsTask` runs the async work on the current thread
    // (synchronously up to the first true async point) instead of
    // queueing onto the thread pool — eliminates the per-request bridge
    // round-trip that `Async.StartAsTask` adds. The interface itself
    // stays on `Async<_>` (GP 12 rule 2 — async at every boundary); the
    // adaptation lives here in the middleware where ASP.NET Core needs
    // a `Task` either way.
    let runAsync (computation: Async<'a>) : System.Threading.Tasks.Task<'a> = Async.StartImmediateAsTask computation

    /// Synthetic-anonymous fallback when subject resolution returns
    /// `Error _`. Uses the request's session id (or a fresh GUID) so
    /// the resulting Subject's storage scope is stable across the
    /// request's downstream calls.
    let fallbackAnonymous (request: SubjectResolutionRequest) : Subject =
        let sid =
            match request.SessionId with
            | Some id when id <> "" -> id
            | _ -> Guid.NewGuid().ToString()

        AnonymousSession sid

    member _.InvokeAsync(ctx: HttpContext) =
        task {
            // Phase 9a: also resolve scope for `/dev/*` so the dev
            // diagnostics endpoint sees the same `AccessContext` /
            // `StorageScope` an API handler would for this request.
            // `SurfaceEnforcementMiddleware` deliberately does *not*
            // gate `/dev/*` — diagnosing auth failures is the
            // endpoint's job.
            let path = ctx.Request.Path
            let isApi = path.StartsWithSegments(PathString "/api")
            let isDev = path.StartsWithSegments(PathString "/dev")

            // Phase 9l — root activity for the request. Inherits parent
            // context from the incoming W3C `traceparent` header so a
            // calling system that already runs OTel (peer service /
            // gateway / external client SDK) shows this request as a
            // child of its own span. Resolved from DI per-request: the
            // no-op default elides every downstream `StartActivity`
            // call. Disposed in the `finally` after `next.Invoke` so
            // the activity's stopwatch covers handler execution +
            // every nested child activity (job / webhook / query bus).
            let activitySink =
                match ctx.RequestServices.GetService(typeof<IActivitySink>) with
                | :? IActivitySink as s -> s
                | _ -> NoOpActivitySink() :> IActivitySink

            let parentContext =
                match ctx.Request.Headers.TryGetValue "traceparent" with
                | true, values when values.Count > 0 -> ActivityContextHelpers.tryParseTraceparent values[0]
                | _ -> None

            let spanName = sprintf "HTTP %s %s" ctx.Request.Method (string ctx.Request.Path)

            let rootActivityOpt = activitySink.StartActivity(spanName, parentContext)

            try
                if isApi || isDev then
                    try
                        let authProv =
                            ctx.RequestServices.GetService(typeof<IAuthProvider>) :?> IAuthProvider

                        let resolver =
                            ctx.RequestServices.GetService(typeof<ISubjectResolver>) :?> ISubjectResolver

                        let! request = runAsync (SubjectRequestExtractor.fromHttpContext ctx authProv)

                        let user = request.User |> Option.defaultValue AuthenticatedUser.anonymous

                        ctx.Items["ToolUp.User"] <- box user
                        ctx.Items["ToolUp.UserId"] <- box user.UserId

                        let! resolved = runAsync (resolver.Resolve request)

                        // Resolved once per request, best-effort: feeds the
                        // Phase 246 fail-closed-default diagnostic (Ok path)
                        // and the resolver-Error downgrade observability
                        // (Error path). `None` when no ILogger is composed.
                        let loggerOpt =
                            match ctx.RequestServices.GetService(typeof<ILogger>) with
                            | :? ILogger as l -> Some l
                            | _ -> None

                        let subject, scopeResult =
                            match resolved with
                            | Ok s ->
                                let scope = StorageScopeDerivation.fromSubject loggerOpt config s
                                s, Ok scope
                            | Error err ->
                                let fallback = fallbackAnonymous request
                                let bridged = SubjectResolutionErrorBridge.toScopeError err
                                // Phase 246 — make the silent authorization-shaped
                                // downgrade observable (distinct from a normal
                                // anonymous request). user.UserId is the resolved
                                // identity whose scope was refused.
                                observeResolverErrorDowngrade ctx loggerOpt err user.UserId
                                fallback, Error bridged

                        ctx.Items["ToolUp.Subject"] <- box subject
                        ctx.Items["ToolUp.ScopeResult"] <- box scopeResult

                        match scopeResult with
                        | Ok scope ->
                            ctx.Items["ToolUp.StorageScope"] <- box scope

                            // Phase 9 audit. Auth providers don't have
                            // a "login" callback (only OIDC's callback
                            // handler does), so we approximate "login"
                            // as "first request from this user inside
                            // a session-length window." Tracked in
                            // `IMemoryCache` keyed by `userId` with a
                            // 20-minute sliding TTL — matches the
                            // ASP.NET Core default `SessionOptions
                            // .IdleTimeout`. Anonymous users don't get
                            // a `UserLoggedIn` event (every anonymous
                            // request would emit one — too noisy).
                            if not (AuthenticatedUser.isAnonymous user) then
                                match
                                    ctx.RequestServices.GetService(typeof<IMemoryCache>),
                                    ctx.RequestServices.GetService(typeof<IAuditLog>)
                                with
                                | (:? IMemoryCache as cache), (:? IAuditLog as auditLog) ->
                                    let cacheKey = "audit:login:" + user.UserId

                                    let mutable existing = Unchecked.defaultof<obj>

                                    if not (cache.TryGetValue(cacheKey, &existing)) then
                                        let entryOpts = MemoryCacheEntryOptions()
                                        entryOpts.SlidingExpiration <- Nullable(TimeSpan.FromMinutes 20.0)
                                        cache.Set(cacheKey, true, entryOpts) |> ignore

                                        // Fire-and-forget — `Record` is
                                        // contractually best-effort and
                                        // swallows its own failures, so
                                        // there's no value in awaiting.
                                        auditLog.Record(
                                            scope.ScopeId,
                                            UserLoggedIn {
                                                UserId = user.UserId
                                                AuthProvider = authProv.GetType().Name
                                            }
                                        )
                                        |> Async.Start

                                        // Phase 3d — pending-invite-by-email
                                        // consumption. Fires on the same
                                        // first-request-per-session trigger
                                        // as the login audit (separate cache
                                        // key so the audit pathway's own
                                        // cache hit doesn't suppress this).
                                        // Distinct from the login audit
                                        // because: (a) it needs IBlobStorage
                                        // + ITeamStore which the audit
                                        // pathway doesn't, and (b) on success
                                        // it mutates state (AddMember),
                                        // making the once-per-session
                                        // semantics load-bearing rather than
                                        // a cost-saving optimisation.
                                        let pendingCacheKey = "pending-invite:check:" + user.UserId

                                        let mutable pendingExisting = Unchecked.defaultof<obj>

                                        if not (cache.TryGetValue(pendingCacheKey, &pendingExisting)) then
                                            let pendingOpts = MemoryCacheEntryOptions()
                                            pendingOpts.SlidingExpiration <- Nullable(TimeSpan.FromMinutes 20.0)
                                            cache.Set(pendingCacheKey, true, pendingOpts) |> ignore

                                            match
                                                user.Email,
                                                ctx.RequestServices.GetService(typeof<IPendingInviteStore>),
                                                ctx.RequestServices.GetService(typeof<ITeamStore>)
                                            with
                                            | Some _,
                                              (:? IPendingInviteStore as pendingStore),
                                              (:? ITeamStore as teamStore) ->
                                                ToolUp.Platform.Teams.TeamInvitationHandler.tryConsumePendingForUser
                                                    pendingStore
                                                    teamStore
                                                    auditLog
                                                    user
                                                |> Async.Ignore
                                                |> Async.Start
                                            | _ -> ()
                                | _ -> ()

                            // Team scopes — load the user's effective
                            // module permissions so the AccessContext
                            // factory and the per-route guard see a
                            // populated map. For non-Team scopes the map
                            // stays empty (unrestricted — opt-in RBAC).
                            if scope.Container.StartsWith "team-" then
                                match ctx.RequestServices.GetService(typeof<IPermissionStore>) with
                                | :? IPermissionStore as permStore ->
                                    let! perms =
                                        runAsync (permStore.GetEffectivePermissions(user.UserId, scope.ScopeId))

                                    ctx.Items["ToolUp.ModulePermissions"] <- box perms

                                    // Phase 245 — the team's per-module exposure
                                    // (tri-state) map, alongside the permission
                                    // map, so the AccessContext factory sees a
                                    // populated `ModuleExposure`.
                                    let! exposure = runAsync (permStore.GetModuleExposure scope.ScopeId)

                                    ctx.Items["ToolUp.ModuleExposure"] <- box exposure
                                | _ -> ()

                            // Phase 4b — resolve PlatformRole per request.
                            // Anonymous callers cannot hold the role
                            // (Platform Admin requires authenticated
                            // identity). For authenticated users the
                            // store is consulted once per request and
                            // the result cached in HttpContext.Items so
                            // the AccessContext factory and any handler
                            // touching `canModifyPlatformConfig` reads
                            // the same value without re-querying.
                            if not (AuthenticatedUser.isAnonymous user) then
                                match ctx.RequestServices.GetService(typeof<IPlatformAdminStore>) with
                                | :? IPlatformAdminStore as adminStore ->
                                    let! isAdmin = runAsync (adminStore.IsPlatformAdmin user.UserId)

                                    if isAdmin then
                                        ctx.Items["ToolUp.PlatformRole"] <- box PlatformRole.PlatformAdmin
                                | _ -> ()
                        | Error _ -> ()
                    with ex ->
                        // Auth-observability A1 — infrastructure error in
                        // the scope-resolution pipeline (DI hiccup, store
                        // throw, cache miss-and-throw). Pre-A1 this catch
                        // was silent: handlers downstream saw missing
                        // Items and fell back to anonymous, and operators
                        // had no signal to distinguish "no credentials"
                        // from "resolver crashed."
                        //
                        // The fail-closed behaviour is preserved (we
                        // don't re-raise — bringing the request path down
                        // for a DI hiccup would be worse). What changes
                        // is observability: structured log + best-effort
                        // audit event so a spike in
                        // `ScopeResolutionFailed` is visible on dashboards
                        // and per-incident triage has a query path.
                        //
                        // Every observability call is itself wrapped in a
                        // local try/with — a logger / audit failure
                        // during the auth-failure path MUST NOT itself
                        // bring the request down.

                        let exceptionKind = ex.GetType().Name
                        let methodName = ctx.Request.Method
                        let pathStr = string ctx.Request.Path

                        try
                            match ctx.RequestServices.GetService(typeof<ILogger>) with
                            | :? ILogger as log ->
                                log.Error(
                                    sprintf
                                        "ScopeResolutionMiddleware swallowed %s on %s %s — request fell through to anonymous: %s"
                                        exceptionKind
                                        methodName
                                        pathStr
                                        ex.Message,
                                    Some ex
                                )
                            | _ -> ()
                        with _ ->
                            ()

                        try
                            match ctx.RequestServices.GetService(typeof<IAuditLog>) with
                            | :? IAuditLog as auditLog ->
                                let truncatedMessage =
                                    let m = ex.Message

                                    if isNull m then "(null)"
                                    elif m.Length <= 512 then m
                                    else m.Substring(0, 512)

                                let correlationId = ToolUp.Remoting.Server.CallContext.correlationId ()

                                auditLog.Record(
                                    "_platform",
                                    AuthScopeResolutionFailed {
                                        Method = methodName
                                        Path = pathStr
                                        ExceptionKind = exceptionKind
                                        Message = truncatedMessage
                                        CorrelationId = correlationId
                                        OccurredAt = DateTimeOffset.UtcNow
                                    }
                                )
                                |> Async.Start
                            | _ -> ()
                        with _ ->
                            ()

                do! next.Invoke(ctx)
            finally
                rootActivityOpt |> Option.iter _.Dispose()
        }
        :> System.Threading.Tasks.Task

// Phase 69a — `emptyArrayJsonBytes` removed along with the retired
// `RemotingBodyNormalizationMiddleware`. The buffer was only used by
// that middleware; the equivalent is now inside ToolUp.Remoting.Giraffe.

/// Gap audit pass-2 #7 — request-id middleware. Generates a per-
/// request id, stores it on `HttpContext.Items`, and stamps both
/// `X-Request-Id` AND `x-correlation-id` headers on the response.
/// Operators correlate audit trail entries / dispatcher logs / request
/// timing logs against the id when investigating "why did request X
/// fail at 14:32".
///
/// Auth-observability A3 — unified correlation id. The middleware
/// now reuses any inbound `x-correlation-id` (the ToolUp.Remoting
/// client-side seam stamps it per Phase 69b.D) so a single id stitches
/// client → server → audit. If neither `x-correlation-id` nor a
/// validated `X-Request-Id` is present, a fresh GUID is minted. The
/// resulting id is published as BOTH headers on the response so legacy
/// readers of either name see the same value.
///
/// Both inbound headers are accepted only when they match a strict
/// shape: 8–64 chars of `[A-Za-z0-9_-]`. Anything else (overlong,
/// wrong character class, non-ASCII) is rejected silently and a
/// fresh id is generated. Defends against log-injection attacks where
/// an attacker passes `X-Request-Id: \r\nfake-log-line` and pollutes
/// log output.
type RequestIdMiddleware(next: RequestDelegate) =
    static let RequestIdHeader = "X-Request-Id"
    static let CorrelationIdHeader = "x-correlation-id"
    static let ContextItemKey = "ToolUp.RequestId"

    static let isValidClientId (id: string) =
        id.Length >= 8
        && id.Length <= 64
        && id
           |> Seq.forall (fun c ->
               (c >= 'A' && c <= 'Z')
               || (c >= 'a' && c <= 'z')
               || (c >= '0' && c <= '9')
               || c = '-'
               || c = '_')

    static let tryReadHeader (ctx: HttpContext) (headerName: string) : string option =
        match ctx.Request.Headers.TryGetValue headerName with
        | true, values when values.Count > 0 ->
            let raw = string values[0]

            if isValidClientId raw then Some raw else None
        | _ -> None

    member _.InvokeAsync(ctx: HttpContext) =
        task {
            // A3 — prefer the unified `x-correlation-id`, fall back to
            // the legacy `X-Request-Id`, finally mint a fresh GUID.
            // Single id stitches all three observability surfaces.
            let requestId =
                match tryReadHeader ctx CorrelationIdHeader with
                | Some id -> id
                | None ->
                    match tryReadHeader ctx RequestIdHeader with
                    | Some id -> id
                    | None -> Guid.NewGuid().ToString("N")

            ctx.Items[ContextItemKey] <- box requestId

            // Stamp BOTH headers on the response so consumers of either
            // name see the same value. Legacy `X-Request-Id` readers
            // continue to work; new consumers grep `x-correlation-id`.
            ctx.Response.OnStarting(fun () ->
                if not (ctx.Response.Headers.ContainsKey RequestIdHeader) then
                    ctx.Response.Headers[RequestIdHeader] <- Microsoft.Extensions.Primitives.StringValues requestId

                if not (ctx.Response.Headers.ContainsKey CorrelationIdHeader) then
                    ctx.Response.Headers[CorrelationIdHeader] <- Microsoft.Extensions.Primitives.StringValues requestId

                System.Threading.Tasks.Task.CompletedTask)

            do! next.Invoke(ctx)
        }
        :> System.Threading.Tasks.Task

// Phase 69a + 69b.A — `RemotingBodyNormalizationMiddleware` retired.
// The body-normalisation behaviour folded into the ToolUp.Remoting.Giraffe
// dispatcher at the Phase 69b.A ship (default `Enabled`; opt-out via
// `Remoting.withoutBodyNormalisation`). No registration in
// `ConfigurePipeline.fs` after Phase 69a's sweep.
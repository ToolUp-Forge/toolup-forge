module ToolUp.Platform.Compose.Middleware

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Scope.PermissionStore
open ToolUp.Platform.Scope.ShareTokenAuth
open ToolUp.Platform.Scope.SurfaceEnforcement
open ToolUp.Platform.Config.TeamManagement
open ToolUp.Platform.Infra.Tracing
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Scope

/// The scope-resolution middleware's own name for a team's permission state
/// that the store answered as unreadable: raised into the one refusal path,
/// so the failure row names what failed rather than a generic exception.
type internal PermissionReadRefused(message: string) =
    inherit Exception(message)

// ─── What the scope-resolution middleware leaves on a request ────────
//
// ONE item: the request's principal, an `AccessContext` under
// `AccessContext.ItemsKey`, stamped once after every per-request grant was
// read. It carries the subject, the provider's user, the storage scope, the
// module permissions and exposure, the platform role, the provider roles and
// admission and the publication grants; every reader reads it through
// `AccessContext.ofItems` (or the scoped `AccessContext` service), never a
// key of its own. Two items stay, because they are written BEFORE the
// principal exists and are inputs to resolving it: the share-token claim
// (`ShareTokenClaimItemsKey`) and the validated service-account principal
// (`ServiceAccountPrincipalItemsKey`). The resolved scope the fact doors read
// (`ScopeResolution.remember`) keeps its own typed accessor.
//
// `AuthEnforcementMiddleware` is retired; `SurfaceEnforcementMiddleware`
// reads `SurfaceRequirementRegistry` per route.

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
        let! user = authProvider.GetUser(ToolUp.Platform.Scope.RequestContextBuilder.ofHttpContext ctx)

        // Phase 337 — the sealed binding cookie is the ONLY source of a
        // verified anonymous session id. When it is present the id it
        // carries wins outright, including over a differing `X-User-Id`:
        // the server-issued value is authoritative and the header is an
        // echo. When it is absent the header value is still carried (it
        // remains useful to a resolver for diagnostics and to the legacy
        // shape), but flagged unverified — so no resolver honouring the
        // contract can let it select a scope.
        let claimedSessionId = sessionIdFrom ctx
        let boundSessionId = AnonymousSessionBinding.boundSessionId ctx

        return {
            User = Some user
            SessionId =
                match boundSessionId with
                | Some bound -> Some bound
                | None -> claimedSessionId
            SessionIdVerified = Option.isSome boundSessionId
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
        | Subject.PublicationReader _ -> "PublicationReader"

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

    /// Phase 1060 — the scope key a subject holds, if it holds one: a
    /// session, a user, a team or a claim. `None` for a `PublicationReader`
    /// (Phase 1002), which holds no scope of its own — no `user-<id>`,
    /// `team-<id>` or session container.
    let tryKeyFromSubject (subject: Subject) : ScopeKey option =
        match subject with
        | AnonymousSession sid -> Some(ScopeKey.Session sid)
        | Subject.AuthenticatedUser uid -> Some(ScopeKey.User(UserId uid))
        | TeamMember(_, tid) -> Some(ScopeKey.Team(TeamId tid))
        | Subject.ClaimBearer claim -> Some(ScopeKey.Claim claim.ScopeId)
        | Subject.PublicationReader _ -> None

    /// Derive the storage scope for a subject, if it holds one: the
    /// subject's `ScopeKey` projected through `StorageScope.ofKey`, with
    /// `Persist` from `ScopeKey.persistence` over the declared `Surfaces`
    /// (Phase 246's rule — a kind no profile declares fails CLOSED to
    /// ephemeral). `logger` (best-effort) receives the one-time diagnostic
    /// when that default fires.
    ///
    /// Phase 1002 — `None` for a `PublicationReader`: the middleware stashes
    /// no `StorageScope` for it and every scope read fails closed. It reads
    /// a publishing scope only through the audience gate of a `Publication`
    /// page that names its readers.
    let tryFromSubject (logger: ILogger option) (config: ServerConfig) (subject: Subject) : StorageScope option =
        tryKeyFromSubject subject
        |> Option.map (fun key ->
            let persistence = ScopeKey.persistence config.Surfaces key

            if persistence.Defaulted then
                warnUndeclaredKindOnce logger subject config

            StorageScope.ofKey persistence.Persist key)

    /// Derive the storage scope for a subject that holds one. Raises for a
    /// `PublicationReader` (Phase 1002), which holds none — a caller that
    /// can meet a reader uses `tryFromSubject`; inventing a container for
    /// it would hand a non-member a scope of its own.
    let fromSubject (logger: ILogger option) (config: ServerConfig) (subject: Subject) : StorageScope =
        match tryFromSubject logger config subject with
        | Some scope -> scope
        | None ->
            invalidArg
                (nameof subject)
                "StorageScopeDerivation.fromSubject: a PublicationReader holds no storage scope (use tryFromSubject)"

/// Map a `SubjectResolutionError` back into the legacy
/// `ScopeResolutionError` shape used by handlers that still read
/// scope-derivation result the middleware decides on.
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
        Some(
            "NotTeamMember",
            sprintf "active-team pointer set to team '%s' but the user is no longer a member" teamId.Value
        )
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
        let warn = Background.writeFailureWarner "ScopeResolutionMiddleware" logger ctx

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
                    AuditScope.platform,
                    Background.requestActor "ScopeResolution" ctx,
                    AuthScopeResolutionFailed {
                        Method = methodName
                        Path = pathStr
                        ExceptionKind = caseLabel
                        Message = sprintf "scope downgraded to anonymous for user '%s': %s" userId detail
                        CorrelationId = correlationId
                        OccurredAt = DateTimeOffset.UtcNow
                    }
                )
                |> Background.startAuditRow warn "scope-downgrade audit"
            | _ -> ()
        with ex ->
            warn "scope-downgrade audit" ex

/// Phase 1002 — a publication reader's sign-in audit. The reader holds no
/// scope, so the member's login audit (which runs where a scope was
/// derived) never fires for it; this is its counterpart. One
/// `PublicationReaderSignedIn` row per reader per session window (the same
/// 20-minute sliding trigger as `UserLoggedIn`, under its own cache key),
/// recorded under `_platform` with the reader (`ReaderAudit`), the roles it
/// was admitted with and the time. It writes no scope and no container.
/// Best-effort: an observability failure never fails the request.
let internal recordPublicationReaderSignIn
    (ctx: HttpContext)
    (authProv: IAuthProvider)
    (user: AuthenticatedUser)
    (grants: PublicationReaderGrant list)
    (readerId: string)
    : unit =
    let warn =
        Background.writeFailureWarner "ScopeResolutionMiddleware" (Background.requestLogger ctx) ctx

    try
        match
            ctx.RequestServices.GetService(typeof<IMemoryCache>), ctx.RequestServices.GetService(typeof<IAuditLog>)
        with
        | (:? IMemoryCache as cache), (:? IAuditLog as auditLog) ->
            let cacheKey = "audit:reader-login:" + readerId
            let mutable existing = Unchecked.defaultof<obj>

            if not (cache.TryGetValue(cacheKey, &existing)) then
                let entryOpts = MemoryCacheEntryOptions()
                entryOpts.SlidingExpiration <- Nullable(TimeSpan.FromMinutes 20.0)
                cache.Set(cacheKey, true, entryOpts) |> ignore

                auditLog.Record(
                    AuditScope.platform,
                    AuditSubject.ReaderAudit(UserId readerId),
                    PublicationReaderSignedIn {
                        SubjectKind = AuditSubject.kindString ReaderAuditKind
                        ReaderId = readerId
                        // Phase 1107 — plus the roles granted in the platform.
                        ReaderRoles = AuthenticatedUser.pageRoles user @ (grants |> List.map _.Role) |> List.distinct
                        AuthProvider = authProv.GetType().Name
                        OccurredAt = DateTimeOffset.UtcNow
                    }
                )
                |> Background.startAuditRow warn "reader sign-in audit"
        | _ -> ()
    with ex ->
        warn "reader sign-in audit" ex

/// Claim a signed-in member's pending team invite, once per session window, BEFORE
/// its subject is resolved. Under team-only surfaces a member with no team has no
/// subject until the invite makes it one, so a later claim would never be reached.
/// A publication reader is never made an app member here.
let private claimPendingInvite (ctx: HttpContext) (user: AuthenticatedUser) : System.Threading.Tasks.Task<unit> = task {
    if
        not (AuthenticatedUser.isAnonymous user)
        && user.Admission = PrincipalAdmission.Member
    then
        match
            ctx.RequestServices.GetService(typeof<IMemoryCache>),
            ctx.RequestServices.GetService(typeof<IAuditLog>),
            user.Email,
            ctx.RequestServices.GetService(typeof<IPendingInviteStore>),
            ctx.RequestServices.GetService(typeof<ITeamStore>)
        with
        | (:? IMemoryCache as cache),
          (:? IAuditLog as auditLog),
          Some _,
          (:? IPendingInviteStore as pendingStore),
          (:? ITeamStore as teamStore) ->
            let key = "pending-invite:check:" + user.UserId.Value

            if not (fst (cache.TryGetValue(box key))) then
                let options = MemoryCacheEntryOptions()
                options.SlidingExpiration <- Nullable(TimeSpan.FromMinutes 20.0)
                cache.Set(key, true, options) |> ignore

                try
                    let! _ =
                        ToolUp.Platform.Scope.Teams.TeamInvitationHandler.tryConsumePendingForUser
                            pendingStore
                            teamStore
                            auditLog
                            user
                        |> Async.StartAsTask

                    ()
                with ex ->
                    Background.writeFailureWarner
                        "ScopeResolutionMiddleware"
                        (Background.requestLogger ctx)
                        ctx
                        "invite consumption"
                        ex
        | _ -> ()
}

/// The request's principal, built ONCE from what this middleware resolved,
/// for `AccessContext.ItemsKey`. `permissions` is the value the middleware
/// decided: the store's answer for a team scope, and `Unrestricted`, stated,
/// for a subject in no team (module RBAC is per team).
///
/// A validated service-account token's declared permission map (the
/// `ServiceAccountPrincipal` the token middleware stamped) takes precedence
/// over `permissions` — precedence rather than merge: the machine
/// principal's declared set IS its authority, and merging it with what the
/// owning team grants its human members would silently widen a credential
/// deliberately scoped narrower. It is adopted as `Restricted`, so an empty
/// set grants nothing. The overlay is this projection and no other.
///
/// `UserId` and `TeamId` come from the Subject (`AccessContext.principalId`),
/// never from the provider's user, which is `"anonymous"` for a
/// service-account bearer it does not recognise. Provider roles, the
/// provider's admission and publication grants ride for a signed-in human
/// principal only.
let internal principalOf
    (ctx: HttpContext)
    (user: AuthenticatedUser)
    (subject: Subject)
    (scope: StorageScope option)
    (permissions: ModulePermissions)
    (exposure: Map<string, ModuleExposure>)
    (platformRole: PlatformRole option)
    (publicationGrants: PublicationReaderGrant list)
    : AccessContext =
    let permissions =
        match ctx.Items.TryGetValue ServiceAccountTokenHandler.ServiceAccountPrincipalItemsKey with
        | true, (:? ServiceAccountPrincipal as account) -> ModulePermissions.Restricted account.Permissions
        | _ -> permissions

    {
        AccessContext.ofSubject permissions subject with
            ModuleExposure = exposure
            PlatformRole = platformRole
            TokenRoles = AccessContext.tokenRolesFor subject (AuthenticatedUser.pageRoles user)
            Admission = AccessContext.admissionFor subject user.Admission
            PublicationGrants = AccessContext.publicationGrantsFor subject publicationGrants
            User = user
            StorageScope = scope
    }

/// ASP.NET Core middleware that resolves the per-request `Subject`,
/// `StorageScope`, and authenticated user, stashing all three on
/// `HttpContext.Items`. See the module preamble for the full key
/// inventory and the Stream A.6 cut-over notes.
///
/// Runs for every request that reaches it — static assets have already
/// been served by `UseStaticFiles` ahead of it in the pipeline. Phase 989:
/// it used to run for `/api/*` and `/dev/*` only, so a page route (the
/// public-rendering catch-all, whose audience gate reads the same
/// `AccessContext`) never had its principal resolved. Page routes now
/// resolve the principal exactly as an API call does, with two
/// differences that keep a public page cheap and cacheable: no anonymous
/// binding cookie is issued, and an ordinary anonymous visitor on a
/// deployment that serves no anonymous surface is not reported as a
/// resolver downgrade. Infrastructure
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
    ///
    /// Phase 337 — the same server-verification gate as the resolver's
    /// step 3. This path is reached on a resolver `Error`, i.e. exactly
    /// when something has already gone wrong, so honouring an unverified
    /// claimed id here would hand an attacker the victim's scope via a
    /// *failed* resolution — the softest door in the building.
    let fallbackAnonymous (request: SubjectResolutionRequest) : Subject =
        let sid =
            match request.SessionId with
            | Some id when id <> "" && request.SessionIdVerified -> id
            | _ -> Guid.NewGuid().ToString()

        AnonymousSession sid

    /// Auth-observability A1 — log and audit (`AuthScopeResolutionFailed`,
    /// under `_platform`) a failure in the scope-resolution pipeline.
    /// `outcome` says what became of the request; `message` is the row's
    /// message, truncated to 512 chars. Every observability call is itself
    /// guarded — a logger / audit failure during the auth-failure path MUST
    /// NOT itself bring the request down.
    let observeResolutionFailure (ctx: HttpContext) (ex: exn) (outcome: string) (message: string) =
        let exceptionKind = ex.GetType().Name
        let methodName = ctx.Request.Method
        let pathStr = string ctx.Request.Path

        let logger = Background.requestLogger ctx

        try
            logger
            |> Option.iter (fun log ->
                log.Error(
                    sprintf
                        "ScopeResolutionMiddleware caught %s on %s %s — %s: %s"
                        exceptionKind
                        methodName
                        pathStr
                        outcome
                        message,
                    Some ex
                ))
        with _ ->
            ()

        let warn = Background.writeFailureWarner "ScopeResolutionMiddleware" logger ctx

        try
            match ctx.RequestServices.GetService(typeof<IAuditLog>) with
            | :? IAuditLog as auditLog ->
                let truncatedMessage =
                    if isNull message then "(null)"
                    elif message.Length <= 512 then message
                    else message.Substring(0, 512)

                let correlationId = ToolUp.Remoting.Server.CallContext.correlationId ()

                auditLog.Record(
                    AuditScope.platform,
                    Background.requestActor "ScopeResolution" ctx,
                    AuthScopeResolutionFailed {
                        Method = methodName
                        Path = pathStr
                        ExceptionKind = exceptionKind
                        Message = truncatedMessage
                        CorrelationId = correlationId
                        OccurredAt = DateTimeOffset.UtcNow
                    }
                )
                |> Background.startAuditRow warn "scope-resolution-failure audit"
            | _ -> ()
        with writeEx ->
            warn "scope-resolution-failure audit" writeEx

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

            // Phase 989 — every other path is a page route (or the SPA
            // shell / a health probe). Its principal is resolved too: an
            // audience-gated SSR page decides 401 / 403 / 200 from the
            // `AccessContext` built out of what this middleware writes, so
            // a page route that skipped resolution was judged against no
            // principal at all.
            let isPageRoute = not (isApi || isDev)

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

            // Set when the team permission read faults, or when ANY fault
            // lands after the first stamp: the request is then refused after
            // the catch-all, never run.
            let refusal: (string * exn) option ref = ref None

            // The module permissions the principal is stamped
            // with: `Unrestricted`, stated, unless a team scope's store
            // answers otherwise below (module RBAC is per team).
            let permissions = ref ModulePermissions.Unrestricted
            let exposure = ref Map.empty<string, ModuleExposure>
            let platformRole: PlatformRole option ref = ref None
            let resolvedScope: StorageScope option ref = ref None
            // Set once the first per-request read that the principal is built
            // from has run: a fault after it refuses (below).
            let stampBegan = ref false

            try
                try
                    let authProv =
                        ctx.RequestServices.GetService(typeof<IAuthProvider>) :?> IAuthProvider

                    let resolver =
                        ctx.RequestServices.GetService(typeof<ISubjectResolver>) :?> ISubjectResolver

                    let! request = runAsync (SubjectRequestExtractor.fromHttpContext ctx authProv)

                    let user = request.User |> Option.defaultValue AuthenticatedUser.anonymous

                    stampBegan.Value <- true

                    do! claimPendingInvite ctx user

                    let! resolvedByResolver = runAsync (resolver.Resolve request)

                    // Phase 1002 — a publication reader's subject does not
                    // depend on the composed resolver or on the deployment's
                    // Surfaces: the same rule `DefaultSubjectResolver` applies
                    // at step 1b, enforced here so a custom resolver can
                    // neither widen a reader into a member's shape nor drop
                    // it to an anonymous session.
                    let resolved =
                        match SubjectResolution.publicationReader request with
                        | Some reader -> Ok reader
                        | None -> resolvedByResolver

                    // Resolved once per request, best-effort: feeds the
                    // Phase 246 fail-closed-default diagnostic (Ok path)
                    // and the resolver-Error downgrade observability
                    // (Error path). `None` when no ILogger is composed.
                    let loggerOpt =
                        match ctx.RequestServices.GetService(typeof<ILogger>) with
                        | :? ILogger as l -> Some l
                        | _ -> None

                    // Phase 1024 — every fire-and-forget write below is
                    // started through `startAudit` with this warner, built
                    // while the request is live.
                    let warn = Background.writeFailureWarner "ScopeResolutionMiddleware" loggerOpt ctx

                    let subject, scopeResult =
                        match resolved with
                        | Ok s ->
                            match StorageScopeDerivation.tryFromSubject loggerOpt config s with
                            | Some scope -> s, Ok scope
                            // Phase 1002 — a publication reader: no scope is
                            // stashed, so every scope read fails closed. The
                            // legacy result says the caller is not an
                            // authenticated app user; the subject stays the
                            // reader (never an anonymous fallback).
                            | None -> s, Error NotAuthenticated
                        | Error err ->
                            let fallback = fallbackAnonymous request
                            let bridged = SubjectResolutionErrorBridge.toScopeError err
                            // Phase 246 — make the silent authorization-shaped
                            // downgrade observable (distinct from a normal
                            // anonymous request). user.UserId is the resolved
                            // identity whose scope was refused.
                            //
                            // Phase 989 — on a page route an anonymous
                            // visitor to a deployment with no anonymous
                            // surface is the ordinary case (a public page
                            // on an authenticated deployment), not a
                            // refused scope, so it is not reported.
                            match err with
                            | SubjectResolutionError.UnsupportedSubject AnonymousKind when isPageRoute -> ()
                            | _ -> observeResolverErrorDowngrade ctx loggerOpt err user.UserId.Value

                            fallback, Error bridged


                    // Phase 1107 — the principal's in-platform publication
                    // reader grants, read per request so a revocation bites
                    // at the next one. Only the `Publication` page audience
                    // reads them, so a member's are read on page routes
                    // only; a reader's on every route (its sign-in audit
                    // names the roles it was admitted with). Nothing is read
                    // where no grant store is composed.
                    let grantHolder =
                        match subject with
                        | PublicationReader userId -> Some userId
                        | AuthenticatedUser userId
                        | TeamMember(userId, _) when isPageRoute -> Some userId
                        | _ -> None

                    let! publicationGrants =
                        match grantHolder, ctx.RequestServices.GetService(typeof<IPublicationReaderGrantStore>) with
                        | Some userId, (:? IPublicationReaderGrantStore as store) ->
                            runAsync (store.GrantsFor(UserId userId))
                        | _ -> System.Threading.Tasks.Task.FromResult []


                    // Phase 337 — seal whatever anonymous session the
                    // request actually resolved to into the binding
                    // cookie, so this browser arrives verified (and
                    // therefore continuous) next time. This is what
                    // makes rejecting an unverified claim above cost
                    // nothing: a first-time visitor is issued a fresh
                    // session here and keeps it thereafter.
                    //
                    // A no-op when the browser is already bound to
                    // this id, so a steady-state anonymous request
                    // emits no `Set-Cookie`. Only anonymous subjects
                    // are bound — an authenticated request neither
                    // needs nor receives one (GP 11 / GP 13).
                    //
                    // Phase 989 — not on a page route: a page needs no
                    // anonymous storage scope, and a `Set-Cookie` on every
                    // public page would make it uncacheable downstream.
                    match subject with
                    | AnonymousSession sid when not isPageRoute -> AnonymousSessionBinding.ensureBound ctx sid
                    | _ -> ()

                    // Phase 1002 — a publication reader's sign-in is audited
                    // under `_platform`: it holds no scope for the member's
                    // login audit below to run under.
                    match subject with
                    | PublicationReader readerId ->
                        recordPublicationReaderSignIn ctx authProv user publicationGrants readerId
                    | _ -> ()

                    match scopeResult with
                    | Ok scope ->
                        resolvedScope.Value <- Some scope

                        // Phase 797 — the fact tier's typed scope is
                        // minted HERE and nowhere else: the resolver's
                        // output becomes a `ResolvedScope` at the one
                        // point the platform resolved it. The fact
                        // doors read it back through
                        // `ScopeResolution.forRequest`.
                        StorageScopeResolver.ScopeResolution.remember ctx scope |> ignore

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
                                let cacheKey = "audit:login:" + user.UserId.Value

                                let mutable existing = Unchecked.defaultof<obj>

                                if not (cache.TryGetValue(cacheKey, &existing)) then
                                    let entryOpts = MemoryCacheEntryOptions()
                                    entryOpts.SlidingExpiration <- Nullable(TimeSpan.FromMinutes 20.0)
                                    cache.Set(cacheKey, true, entryOpts) |> ignore

                                    // Phase 1024 — fire-and-forget, but
                                    // observed: `Record` is contractually
                                    // best-effort, yet a sink that breaks
                                    // the contract must not kill the host
                                    // or (thrown synchronously) fall
                                    // through to the anonymous fallback.
                                    try
                                        auditLog.Record(
                                            StorageScope.auditScope scope,
                                            AuditSubject.fromSubject subject,
                                            UserLoggedIn {
                                                UserId = user.UserId
                                                AuthProvider = authProv.GetType().Name
                                            }
                                        )
                                        |> Background.startAuditRow warn "login audit"
                                    with ex ->
                                        warn "login audit" ex
                            | _ -> ()

                        // Team scopes — load the user's effective
                        // module permissions; the store decides whether
                        // the team configured any. For
                        // non-Team scopes no RBAC applies and the
                        // principal is stamped `Unrestricted`.
                        // Phase 1060 — "is this a team scope" is read
                        // through the one parser, not a prefix test.
                        let isTeamScope =
                            match ScopeKey.tryParseContainer scope.Container with
                            | Some(ScopeKey.Team _) -> true
                            | _ -> false

                        if isTeamScope then
                            match ctx.RequestServices.GetService(typeof<IPermissionStore>) with
                            | :? IPermissionStore as permStore ->
                                // A fault reading this team's permission state
                                // REFUSES the request (below, after the catch-all): left to
                                // the catch-all it was swallowed, the permission map was never
                                // stamped, and the empty map the access-context factory then
                                // reads admits every module.
                                // The store answers an unreadable document as data; it is
                                // raised HERE, into the catch below, so every way this read
                                // can fail reaches the one refusal.
                                let refusedOn (read: Result<'T, PermissionStoreError>) : 'T =
                                    match read with
                                    | Ok value -> value
                                    | Error e -> raise (PermissionReadRefused(PermissionStoreError.describe e))

                                try
                                    let! perms =
                                        runAsync (permStore.GetEffectivePermissions(user.UserId, TeamId scope.ScopeId))

                                    permissions.Value <- refusedOn perms

                                    // Phase 245 — the team's per-module exposure
                                    // (tri-state) map, alongside the permission
                                    // map, so the AccessContext factory sees a
                                    // populated `ModuleExposure`.
                                    let! exposureRead = runAsync (permStore.GetModuleExposure(TeamId scope.ScopeId))

                                    exposure.Value <- refusedOn exposureRead

                                    // Phase 551 — the subject's grant
                                    // records, so the module-access gate
                                    // can re-verify a declared
                                    // `GrantPolicy` ON USE rather than
                                    // trusting that the write path
                                    // enforced it.
                                    //
                                    // Loaded ONLY when at least one module
                                    // declares a policy stricter than
                                    // `AdminDiscretion`. A deployment that
                                    // declares none registers no registry,
                                    // so this is one failed `GetService`
                                    // and no extra store read (GP 13) —
                                    // and the dispatch gate answers
                                    // `AdminDiscretion` for every module,
                                    // which is unconditionally live.
                                    match
                                        ctx.RequestServices.GetService(
                                            typeof<GrantPolicyGuard.ModuleGrantPolicyRegistry>
                                        )
                                    with
                                    | :? GrantPolicyGuard.ModuleGrantPolicyRegistry as registry when
                                        not (GrantPolicyGuard.ModuleGrantPolicyRegistry.isEmpty registry)
                                        ->
                                        let! doc = runAsync (permStore.GetTeamPermissions(TeamId scope.ScopeId))
                                        let doc = refusedOn doc

                                        let subjectGrants = TeamPermissions.grantsFor user.UserId.Value doc

                                        ctx.Items[GrantPolicyGuard.ModuleGrantsItemsKey] <- box subjectGrants

                                        // Phase 552 — resolve the consent
                                        // verdict for each module the
                                        // subject holds a grant record on
                                        // that declares
                                        // `RequiresCounterpartyApproval`.
                                        // Resolved PER REQUEST rather than
                                        // cached, which is the whole of
                                        // 552.D: a revocation takes effect
                                        // at the very next call, not at the
                                        // next sweep. The set is bounded by
                                        // the subject's own counterparty
                                        // grants — typically none, and never
                                        // more than the modules that
                                        // deployment declared — so the cost
                                        // is zero for every deployment that
                                        // declares no counterparty arm, and
                                        // one registry read per genuinely
                                        // counterparty-gated module
                                        // otherwise (GP 13).
                                        let counterpartyModules =
                                            subjectGrants
                                            |> Map.toList
                                            |> List.choose (fun (moduleName, _) ->
                                                match
                                                    GrantPolicyGuard.ModuleGrantPolicyRegistry.resolve
                                                        registry
                                                        moduleName
                                                with
                                                | GrantPolicy.RequiresCounterpartyApproval party ->
                                                    Some(moduleName, party)
                                                | _ -> None)

                                        if not (List.isEmpty counterpartyModules) then
                                            let consentStore =
                                                match
                                                    ctx.RequestServices.GetService(
                                                        typeof<GrantConsentStore.IGrantConsentStore>
                                                    )
                                                with
                                                | :? GrantConsentStore.IGrantConsentStore as s -> Some s
                                                | _ -> None

                                            let verifier =
                                                match
                                                    ctx.RequestServices.GetService(
                                                        typeof<GrantConsentStore.IGrantConsentVerifier>
                                                    )
                                                with
                                                | :? GrantConsentStore.IGrantConsentVerifier as v -> v
                                                // No verifier composed ⇒
                                                // nothing can be checked ⇒
                                                // nothing is admitted. Never
                                                // a fall-through to "trust
                                                // the record".
                                                | _ -> GrantConsentStore.denyingVerifier

                                            let auditLog = Background.auditLogOf ctx

                                            let mutable verdicts = Map.empty<string, Result<unit, ConsentDenial>>

                                            for moduleName, party in counterpartyModules do
                                                let subject =
                                                    ConsentSubject.create scope.ScopeId user.UserId.Value moduleName

                                                let! resolved =
                                                    runAsync (
                                                        GrantConsentStore.resolveLive
                                                            consentStore
                                                            verifier
                                                            auditLog
                                                            DateTimeOffset.UtcNow
                                                            subject
                                                            party
                                                    )

                                                verdicts <-
                                                    verdicts |> Map.add moduleName (resolved |> Result.map ignore)

                                            ctx.Items[GrantConsentStore.ModuleGrantConsentsItemsKey] <- box verdicts
                                    | _ -> ()
                                with ex ->
                                    refusal.Value <-
                                        Some(
                                            sprintf
                                                "IPermissionStore (%s) fault: %s"
                                                (permStore.GetType().Name)
                                                ex.Message,
                                            ex
                                        )
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
                        if refusal.Value.IsNone && not (AuthenticatedUser.isAnonymous user) then
                            match ctx.RequestServices.GetService(typeof<IPlatformAdminStore>) with
                            | :? IPlatformAdminStore as adminStore ->
                                let! isAdmin = runAsync (adminStore.IsPlatformAdmin(user.UserId))

                                if isAdmin then
                                    platformRole.Value <- Some PlatformRole.PlatformAdmin
                            | _ -> ()
                    | Error _ -> ()

                    // The principal, stamped ONCE, after every
                    // per-request grant above was read. A request this line
                    // did not run for has no principal: the access-context
                    // factory yields no context and every reader refuses.
                    if refusal.Value.IsNone then
                        AccessContext.stamp
                            ctx.Items
                            (principalOf
                                ctx
                                user
                                subject
                                resolvedScope.Value
                                permissions.Value
                                exposure.Value
                                platformRole.Value
                                publicationGrants)
                with ex ->
                    // Auth-observability A1 — infrastructure error in
                    // the scope-resolution pipeline (DI hiccup, resolver
                    // throw, cache miss-and-throw), observed through a
                    // structured log + best-effort audit event so a spike
                    // in `ScopeResolutionFailed` is visible on dashboards.
                    //
                    // What this catch protects: a fault BEFORE the first
                    // per-request read. No principal is stamped, and
                    // `SurfaceEnforcementMiddleware` treats a missing
                    // principal on an `/api/*` path as fail-closed — a fresh
                    // `AnonymousSession` judged by the SAME surface gate —
                    // while every handler serves it under a context that
                    // grants nothing.
                    //
                    // A fault AFTER the first stamp (the
                    // provider's user) is not swallowed. There a downstream
                    // reader would see a user or a subject with no
                    // principal: the surface gate would pass a valid member
                    // and the dispatcher an authenticated caller, whatever
                    // per-request grant the fault left unread. So it is
                    // REFUSED with a 503 below, as a permission-store fault
                    // is, whichever read faulted.
                    if stampBegan.Value then
                        refusal.Value <-
                            Some(sprintf "%s after the principal stamp began: %s" (ex.GetType().Name) ex.Message, ex)
                    else
                        observeResolutionFailure ctx ex "request fell through to anonymous" ex.Message

                match refusal.Value with
                | Some(detail, ex) ->
                    // The principal could not be resolved
                    // whole: refuse rather than run the request without one.
                    // A 503, because a dependency failed, not the caller.
                    observeResolutionFailure ctx ex "request refused with 503" detail

                    ctx.Response.StatusCode <- StatusCodes.Status503ServiceUnavailable
                | None -> do! next.Invoke(ctx)
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
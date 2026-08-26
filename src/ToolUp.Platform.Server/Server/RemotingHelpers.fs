module ToolUp.Platform.RemotingHelpers

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.FileManagement

/// Shared diagnostic logging for Fable.Remoting error handlers. Routes
/// through the DI-registered `ILogger`. `compose` validates the
/// registration once at startup and throws if missing, so this lookup
/// never sees a missing service when the request was reached through
/// the standard composition pipeline. Extracted so the plain `makeApi`
/// and the permission-guarded variant share the same diagnostic path.
let private logApiError ex (routeInfo: ToolUp.Remoting.Server.RouteInfo<HttpContext>) =
    let logger =
        ServiceProviderServiceExtensions.GetRequiredService<ILogger>(routeInfo.httpContext.RequestServices)

    logger.Error($"Fable.Remoting error on {routeInfo.path}", Some ex)

/// Classify Fable.Remoting exceptions that represent user-action errors
/// rather than server faults. Returns `Some result` when the exception
/// is recognised — the caller short-circuits with that result instead of
/// logging at `Error` with a stack trace. Today this is just the
/// "file not in session" case (user clicked Run before uploading); add
/// new cases here as more user-error classes get typed exceptions.
let private tryClassifyUserError ex (routeInfo: ToolUp.Remoting.Server.RouteInfo<HttpContext>) =
    match ex with
    | FileNotFoundInSessionException msg ->
        routeInfo.httpContext.Response.StatusCode <- 400

        let logger =
            ServiceProviderServiceExtensions.GetRequiredService<ILogger>(routeInfo.httpContext.RequestServices)

        logger.Warn($"Fable.Remoting user error on {routeInfo.path}: {msg}")
        Some(ToolUp.Remoting.Server.ErrorResult.Propagate msg)
    | _ -> None

/// Create a Fable.Remoting API handler with standard error handling.
/// Errors are logged via the DI `ILogger` and propagated to the client
/// so the UI can surface actionable messages.
///
/// The explicit `<'impl>` type parameter is load-bearing. With an
/// un-annotated parameter (`let makeApi api = ...`) F# failed to generalise
/// `api`, monomorphising `makeApi` to the first call site's type. Every
/// `makeApi`-composed handler then shared ONE compile-time `'impl` — and thus
/// ONE `AuthClassifier.classify typeof<'impl>` map (whichever API pinned it,
/// e.g. `PlatformInfoApi`). Routing still worked (it reflects the runtime
/// record value), but the Phase 69d auth pre-flight evaluated EVERY endpoint
/// against that single API's classifications, so every other method missed
/// and fail-closed as `Unclassified` once an auth resolver was composed
/// (`/health`, `/api/FileManagementApi/ListFiles`, … all 401'd). Annotating
/// the context-factory shape (`HttpContext -> 'impl`) forces per-call-site
/// generalisation — matching the explicitly-generic `permissionGuardedApiCore<'T>`
/// module path, which never had the bug. Every caller is already a
/// `HttpContext -> 'ApiRecord` factory.
let makeApi<'impl> (api: HttpContext -> 'impl) =
    let errorHandler ex routeInfo =
        match tryClassifyUserError ex routeInfo with
        | Some result -> result
        | None ->
            logApiError ex routeInfo
            ToolUp.Remoting.Server.ErrorResult.Propagate ex

    Api.make (api, errorHandler = errorHandler)

/// Module-access gate + standard error handling shared by
/// `ServerModule.withGuardedApi` (the canonical module composition path)
/// and the obsolete public `makePermissionGuardedApi` shim below. Gates
/// a module's routes on `AccessContext.canAccessModule moduleName` —
/// module-level RBAC (per-team module grants via `IPermissionStore`),
/// which is a DIFFERENT axis from Phase 69d's per-method authorisation
/// attributes and therefore survives their adoption.
///
/// Denials raise `UnauthorizedAccessException`; the error handler
/// translates that to HTTP 403 with the access-denied message as the
/// response body. Teams with no permission config configured still
/// see every module (empty map = unrestricted — opt-in RBAC).
///
/// `moduleName` is the ToolUp module identifier (e.g. "SkuAnalysis")
/// — matches the keys used by `AccessContext.ModulePermissions` and
/// `IPermissionStore`.
let internal permissionGuardedApiCore<'T> (moduleName: string) (apiBuilder: HttpContext -> 'T) : HttpHandler =
    // Phase 551 — the module's declared `GrantPolicy`, re-verified ON USE.
    //
    // The registry is resolved per request rather than closed over,
    // because `permissionGuardedApiCore` runs at `ServerModule.withGuardedApi`
    // time — before `compose` has built the container the registry lives
    // in. The lookup misses entirely on a deployment that declares no
    // policy (nothing is registered — GP 13), and `resolve` then answers
    // `AdminDiscretion`, for which `isGrantLive` is unconditionally true.
    // Such a deployment therefore executes one failed `GetService` and is
    // otherwise byte-for-byte its pre-551 self.
    let resolveRegistry (ctx: HttpContext) =
        match ctx.RequestServices.GetService(typeof<GrantPolicyGuard.ModuleGrantPolicyRegistry>) with
        | :? GrantPolicyGuard.ModuleGrantPolicyRegistry as r -> r
        | _ -> GrantPolicyGuard.ModuleGrantPolicyRegistry.empty

    /// Refuse + audit a permission entry that is present but inert. This
    /// is the control that survives a grant row written straight into the
    /// store: the write guard can be bypassed, this cannot (Phase 311).
    /// The decision and the audit row both come from
    /// `GrantPolicyGuard.guardDispatch`, so the pack exercises the same
    /// path a request takes rather than a re-implementation of it.
    let assertGrantLive (ctx: HttpContext) (accessCtx: AccessContext) =
        let auditLog =
            match ctx.RequestServices.GetService(typeof<IAuditLog>) with
            | :? IAuditLog as log -> Some log
            | _ -> None

        let scopeId =
            accessCtx.TeamId
            |> Option.map (fun t -> $"team-{t}")
            |> Option.defaultValue accessCtx.UserId

        // Phase 552 — the same control, extended by exactly one arm:
        // `RequiresCounterpartyApproval` now resolves through the consent
        // registry (via the verdicts `ScopeResolutionMiddleware` stamped
        // for THIS request) instead of refusing unconditionally. Every
        // other arm delegates to the Phase 551 guard unchanged, and an
        // empty registry still short-circuits before anything is read —
        // so a deployment declaring no policy is byte-for-byte its
        // pre-551 self, and one declaring no COUNTERPARTY policy is
        // byte-for-byte its pre-552 self.
        match
            GrantConsentStore.guardDispatchWithConsent
                (resolveRegistry ctx)
                (GrantPolicyGuard.grantsFromItems ctx.Items)
                (GrantConsentStore.consentVerdictsFromItems ctx.Items)
                auditLog
                Async.Start
                scopeId
                accessCtx.UserId
                moduleName
        with
        | Ok() -> ()
        | Error payload ->
            raise (
                UnauthorizedAccessException(
                    $"Access denied to module '{moduleName}': the grant is not live under the module's declared grant policy '{payload.DeclaredPolicy}' ({payload.InertReason})."
                )
            )

    let guardedBuilder (ctx: HttpContext) : 'T =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as accessCtx when not (AccessContext.canAccessModule moduleName accessCtx) ->
            raise (UnauthorizedAccessException($"Access denied to module '{moduleName}'"))
        | :? AccessContext as accessCtx ->
            assertGrantLive ctx accessCtx
            apiBuilder ctx
        | _ -> apiBuilder ctx

    let errorHandler (ex: exn) (routeInfo: ToolUp.Remoting.Server.RouteInfo<HttpContext>) =
        match ex with
        | :? UnauthorizedAccessException ->
            routeInfo.httpContext.Response.StatusCode <- 403
            ToolUp.Remoting.Server.ErrorResult.Propagate ex.Message
        | _ ->
            match tryClassifyUserError ex routeInfo with
            | Some result -> result
            | None ->
                logApiError ex routeInfo
                ToolUp.Remoting.Server.ErrorResult.Propagate ex

    Api.make (guardedBuilder, errorHandler = errorHandler)

/// `makeApi` variant that gates a module's routes on
/// `AccessContext.canAccessModule moduleName`.
///
/// Phase 69d.tail — obsolete as a public entry point. Compose modules
/// via `ServerModule.withGuardedApi` (which carries the same module-
/// access gate), and declare method-level authorisation with the
/// per-method attributes (`[<RequiresRole>]` / `[<RequiresClaim>]` /
/// `[<TenantScoped>]` / `[<AllowAnonymous>]` / `[<PublicEndpoint>]`)
/// that the dispatcher's startup classifier now enforces default-on.
/// Deletion target: next major version.
[<Obsolete("Compose modules via ServerModule.withGuardedApi and declare method-level authorisation with per-method attributes ([<RequiresRole>] / [<TenantScoped>] / [<AllowAnonymous>] / ...) — the startup classifier enforces them default-on (Phase 69d.tail). See docs/migrations/69d-authorization-metadata.md.")>]
let makePermissionGuardedApi<'T> (moduleName: string) (apiBuilder: HttpContext -> 'T) : HttpHandler =
    permissionGuardedApiCore<'T> moduleName apiBuilder
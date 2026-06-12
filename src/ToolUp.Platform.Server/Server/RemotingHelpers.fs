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
let makeApi api =
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
    let guardedBuilder (ctx: HttpContext) : 'T =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as accessCtx when not (AccessContext.canAccessModule moduleName accessCtx) ->
            raise (UnauthorizedAccessException($"Access denied to module '{moduleName}'"))
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
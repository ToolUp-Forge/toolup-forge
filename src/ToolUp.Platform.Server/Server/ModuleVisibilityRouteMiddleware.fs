module ToolUp.Platform.ModuleVisibilityRoutes

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Phase 637 — opt-in route hardening ──────────────────────────────
//
// `ServerConfig.ModuleVisibility = EnforcedModuleVisibility` turns a
// visibility profile from a surfacing decision into a route one: an
// `/api/*` request under a route prefix declared by an EXCLUDED module
// is answered 404.
//
// **What this is and is not.** It is hardening — closing the gap where a
// curated deployment still answers a bookmark to a module the operator
// removed from the surface. It is NOT the authorization boundary: the
// per-route guards (`SurfaceEnforcementMiddleware`, the per-module
// permission guard, `[<RequiresRole>]` / `[<TenantScoped>]`
// classification) remain the enforcement, and a profile never widens
// what they permit — it can only subtract (GP 12).
//
// **404, not 403.** The claim a profile makes is "this module is not
// part of this deployment's surface for you". A 403 would instead
// confirm the module exists and refuse it, which contradicts the claim
// and hands a prober a module inventory. This mirrors the SDK's existing
// posture for a subsystem the deployment never composed: its routes are
// simply not there.
//
// **Reach.** Enforcement can only see routes a module DECLARES, via
// `ServerModule.RoutePrefixes`. A module that declares none is
// unaffected — its endpoints are indistinguishable at the path level
// from any other module's, because the Fable.Remoting route builder
// names the API RECORD TYPE, not the module. That limit is stated in
// `ModuleVisibilityMode.EnforcedModuleVisibility` and in the docs page,
// rather than papered over: a hardening mechanism whose coverage is
// implicit is worse than one whose coverage is narrow and written down.

/// Compose-time map from a module id to the route prefixes it declares.
/// Built from the accumulated `ServerModule.RoutePrefixes` and registered
/// as a DI singleton; empty on a deployment whose modules declare no
/// prefixes, in which case the middleware is a pure pass-through.
///
/// Value-typed (like `SurfaceRequirementRegistry`) so the composition
/// root hands the middleware plain data rather than a live service.
type ModuleRouteRegistry = {
    /// `(moduleId, routePrefix)` pairs, prefix lower-cased at build time
    /// so resolution is a plain `StartsWith` — the same normalisation
    /// `SurfaceRequirementRegistry` applies, and for the same reason
    /// (clients differ on path casing).
    Prefixes: (string * string) list
    /// The deployment's registered module ids (`ServerConfig.ModuleNames`).
    /// Carried here rather than injected separately so the middleware has
    /// exactly ONE DI-resolved constructor parameter — a `string list` in
    /// the container would be an unnamed, collision-prone registration for
    /// no gain.
    RegisteredModuleIds: string list
}

module ModuleRouteRegistry =
    /// The inert registry — no module declares a prefix, so no request
    /// can ever be attributed to a module and the middleware never acts.
    let empty: ModuleRouteRegistry = {
        Prefixes = []
        RegisteredModuleIds = []
    }

    /// Build from raw `(moduleId, prefix)` declarations, dropping blank
    /// prefixes (a module declaring `""` would otherwise claim every
    /// path, turning one mis-declaration into a deployment-wide 404).
    let create (registeredModuleIds: string list) (declarations: (string * string) list) : ModuleRouteRegistry = {
        Prefixes =
            declarations
            |> List.choose (fun (moduleId, prefix) ->
                match prefix with
                | null -> None
                | p when String.IsNullOrWhiteSpace p -> None
                | p -> Some(moduleId, p.ToLowerInvariant()))
        RegisteredModuleIds = registeredModuleIds
    }

    /// Which module owns this path, if any? Longest-prefix wins, the
    /// same tie-break `SurfaceRequirementRegistry.resolve` uses — a
    /// module mounted under a sub-tree of another's prefix must win over
    /// its parent, else the more specific declaration is unreachable.
    let owningModule (registry: ModuleRouteRegistry) (path: string) : string option =
        let pathNormalised =
            match path with
            | null -> ""
            | p -> p.ToLowerInvariant()

        registry.Prefixes
        |> List.filter (fun (_, prefix) -> pathNormalised.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        |> List.sortByDescending (fun (_, prefix) -> prefix.Length)
        |> List.tryHead
        |> Option.map fst

/// ASP.NET Core middleware refusing `/api/*` requests to modules the
/// caller's resolved visibility profile excludes.
///
/// Registered ONLY when `ServerConfig.ModuleVisibility =
/// EnforcedModuleVisibility`, so the other two modes pay nothing — not
/// even a per-request delegate hop (GP 13). Must sit AFTER
/// `ScopeResolutionMiddleware` in the pipeline, because the scoped
/// `AccessContext` it resolves is built from the items that middleware
/// stashes.
///
/// **Cost.** Resolving a profile is up to three blob reads, so the
/// middleware resolves ONLY when the path is attributable to a module at
/// all — a request under no declared prefix short-circuits before any
/// I/O. On a deployment whose modules declare prefixes and whose
/// operators have opted into enforcement, that is one resolution per
/// module-route request; the trade is deliberate and stated here so the
/// next reader does not have to measure it to find out.
type ModuleVisibilityRouteMiddleware(next: RequestDelegate, registry: ModuleRouteRegistry) =

    member _.InvokeAsync(ctx: HttpContext) =
        task {
            let path = ctx.Request.Path

            if not (path.StartsWithSegments(PathString "/api")) then
                do! next.Invoke ctx
            else
                match ModuleRouteRegistry.owningModule registry (string path) with
                | None ->
                    // Not attributable to any module — nothing a profile
                    // could have an opinion about.
                    do! next.Invoke ctx
                | Some moduleId ->
                    let storeOpt =
                        match ctx.RequestServices.GetService(typeof<IModuleVisibilityStore>) with
                        | :? IModuleVisibilityStore as s -> Some s
                        | _ -> None

                    let accessCtxOpt =
                        match ctx.RequestServices.GetService(typeof<AccessContext>) with
                        | :? AccessContext as ac -> Some ac
                        | _ -> None

                    match storeOpt, accessCtxOpt with
                    | Some store, Some accessCtx ->
                        let! resolution =
                            ModuleVisibilityResolver.resolveFor store registry.RegisteredModuleIds accessCtx

                        if ModuleVisibility.admitsModuleOpt resolution moduleId then
                            do! next.Invoke ctx
                        else
                            ctx.Response.StatusCode <- 404
                            ctx.Response.ContentType <- "application/json"
                            do! ctx.Response.WriteAsync """{"error":"not_found","status":404}"""
                    | _ ->
                        // Enforcement was requested but the substrate it
                        // needs is not resolvable. Fail OPEN rather than
                        // 404-ing every module route: this middleware is
                        // hardening layered on top of guards that are
                        // still doing their job, so a substrate gap must
                        // degrade to the un-hardened behaviour, never to a
                        // deployment-wide outage. The compose path
                        // registers the store alongside this middleware,
                        // so the branch is unreachable in a composed app
                        // and exists for hand-built pipelines and tests.
                        do! next.Invoke ctx
        }
        :> System.Threading.Tasks.Task
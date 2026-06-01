namespace ToolUp.Platform

// Server-side `Api.make` helper — replicates the surface of
// `SAFE.Api.make` from SAFE.Server.Utils (MIT, Copyright 2024
// Compositional IT) so server call sites can continue using
// `Api.make (builder, errorHandler = eh)` unchanged after the
// ToolUp.Platform SAFE removal. See Shared/Api.fs for the DU half
// (`ApiCall`, `RemoteData`) and Client/Api.fs for the Fable-side
// `Api.makeProxy` helper.
//
// This file is injected via ToolUp.Platform.Server.props into every
// server-hosting project; ToolUp.Platform.dll itself intentionally
// does not depend on ToolUp.Remoting.Giraffe.
//
// 0.4.1 — gained an optional `authContext` argument that bridges
// the forge-native auth attributes in `ToolUp.Platform.Core` to
// `ToolUp.Remoting.Server`'s Phase 69d `IAuthContext` resolver. The
// bridge is opt-in: passing `?authContext` enables the per-method
// classifier (and the startup-time refusal for unannotated methods);
// omitting it preserves the pre-0.4.1 zero-cost behaviour.
//
// 0.4.3 — closes the silent-no-op trap. If the API record `'T`
// carries any of the forge auth attributes (`RequiresRole`,
// `RequiresClaim`, `TenantScoped`, `AllowAnonymous`,
// `PublicEndpoint`) but no `authContext` resolver is supplied, the
// dispatcher would previously ship those methods with attributes
// declared but enforcement skipped — a silent insecure default.
// `Api.make` now refuses to start in that case, naming the
// attributed members so the wiring defect surfaces at composition
// time rather than at runtime.

open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe
open Giraffe

/// 0.4.1 — forge-side `IAuthContext` adapter shape. Consumers supply
/// an `HttpContext -> Async<IAuthContext>` resolver to `Api.make`;
/// the resolver reads the per-request subject (from the auth pipeline
/// already in `ToolUp.Platform.Server.Middleware`) and returns an
/// `IAuthContext` the dispatcher evaluates against `[<RequiresRole>]`
/// / `[<RequiresClaim>]` / `[<TenantScoped>]` / `[<AllowAnonymous>]` /
/// `[<PublicEndpoint>]` attributes declared on the API record fields.
type ForgeAuthContext =
    abstract HasRole: role: string -> bool
    abstract HasClaim: claim: string * value: string option -> bool
    abstract HasTenant: unit -> bool
    abstract IsAnonymous: unit -> bool
    abstract SubjectId: string

/// Server-side Fable Remoting helper. Mirrors SAFE.Api.make so server
/// call sites keep using `Api.make (builder, errorHandler = eh)`.
type Api =
    /// Build a Fable Remoting HttpHandler from an `HttpContext -> 'T`
    /// api builder. Matches SAFE.Api.make's signature: optional route
    /// builder, error handler, and remoting-options customiser.
    ///
    /// 0.4.1 — gained optional cross-cutting composers:
    ///
    /// * `authContext: HttpContext -> Async<ForgeAuthContext>` — opt
    ///   into Phase 69d auth-attribute enforcement. The dispatcher
    ///   evaluates each method's auth attributes against the resolved
    ///   `ForgeAuthContext` per request. If *any* field on `'T` is
    ///   unclassified at startup, the dispatcher refuses to start
    ///   naming the offending methods.
    ///
    /// * `telemetry: IRemotingTelemetry` — opt into Phase 69b.C
    ///   per-method telemetry emission. Composed at this layer so
    ///   handlers don't have to wire `MetricsMiddleware` per route.
    ///
    /// 0.4.3 — gates the silent-no-op trap. Omitting `authContext`
    /// while `'T` carries any forge auth attribute now fails at
    /// composition with the offending member list; the dispatcher
    /// no longer silently ships unenforced attributes. A record with
    /// no forge auth attributes is still free to omit `authContext`
    /// and keeps the pre-0.4.1 zero-cost behaviour.
    static member make<'T>
        (
            api: HttpContext -> 'T,
            ?routeBuilder: string -> string -> string,
            ?errorHandler: exn -> RouteInfo<HttpContext> -> ErrorResult,
            ?customOptions: RemotingOptions<HttpContext, 'T> -> RemotingOptions<HttpContext, 'T>,
            ?authContext: HttpContext -> Async<ForgeAuthContext>,
            ?telemetry: IRemotingTelemetry
        ) : HttpHandler =
        let routeBuilder = defaultArg routeBuilder (sprintf "/api/%s/%s")
        let customOptions = defaultArg customOptions id

        // 0.4.3 — silent-no-op guard. If `'T` declares any forge auth
        // attribute and no resolver is supplied, refuse at composition.
        // Without this, the dispatcher would happily route requests
        // ignoring the declared attributes — a class of insecure
        // default an OSS adopter following the samples would land on.
        if authContext.IsNone then
            let forgeAuthAttrFullNames =
                Set.ofList [
                    typeof<RequiresRoleAttribute>.FullName
                    typeof<RequiresClaimAttribute>.FullName
                    typeof<TenantScopedAttribute>.FullName
                    typeof<AllowAnonymousAttribute>.FullName
                    typeof<PublicEndpointAttribute>.FullName
                ]

            let t = typeof<'T>

            let attributed = [
                for p in t.GetProperties() do
                    for a in p.GetCustomAttributes(false) do
                        if forgeAuthAttrFullNames.Contains(a.GetType().FullName) then
                            yield p.Name, a.GetType().Name
                for f in t.GetFields() do
                    for a in f.GetCustomAttributes(false) do
                        if forgeAuthAttrFullNames.Contains(a.GetType().FullName) then
                            yield f.Name, a.GetType().Name
            ]

            if not (List.isEmpty attributed) then
                let lines =
                    attributed
                    |> List.map (fun (m, a) -> sprintf "    %s : [<%s>]" m a)
                    |> String.concat System.Environment.NewLine

                failwithf
                    "Api.make: API record '%s' carries forge auth attributes:%s%s%sbut no `authContext` resolver was supplied. The dispatcher would silently skip enforcement. Pass `?authContext = Some (fun ctx -> async { ... })` to enable Phase 69d auth-attribute evaluation, or remove the attributes if enforcement is intentionally handled elsewhere."
                    t.Name
                    System.Environment.NewLine
                    lines
                    System.Environment.NewLine

        // Bridge: ForgeAuthContext (declared on Platform.Core) →
        // IAuthContext (declared on ToolUp.Remoting.Server). Trivial
        // member-by-member adapter; isolates Platform.Core from a
        // ToolUp.Remoting.Server dependency.
        let bridgeAuth: (HttpContext -> Async<IAuthContext>) option =
            match authContext with
            | None -> None
            | Some resolver ->
                Some(fun ctx -> async {
                    let! forge = resolver ctx

                    return
                        { new IAuthContext with
                            member _.HasRole role = forge.HasRole role
                            member _.HasClaim(claim, value) = forge.HasClaim(claim, value)
                            member _.HasTenant() = forge.HasTenant()
                            member _.IsAnonymous() = forge.IsAnonymous()
                            member _.SubjectId = forge.SubjectId
                        }
                })

        Remoting.createApi ()
        |> Remoting.withRouteBuilder routeBuilder
        |> Remoting.fromContext api
        |> (match errorHandler with
            | Some eh -> Remoting.withErrorHandler eh
            | None -> id)
        |> (match bridgeAuth with
            | Some resolver -> Remoting.withAuthContext resolver
            | None -> id)
        |> (match telemetry with
            | Some sink -> Remoting.withTelemetry sink
            | None -> id)
        |> customOptions
        |> Remoting.buildHttpHandler
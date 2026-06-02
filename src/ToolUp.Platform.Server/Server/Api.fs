namespace ToolUp.Platform

// Server-side `Api.make` helper — replicates the surface of
// `SAFE.Api.make` from SAFE.Server.Utils (MIT, Copyright 2024
// Compositional IT) so server call sites can continue using
// `Api.make (builder, errorHandler = eh)` unchanged after the
// ToolUp.Platform SAFE removal. See Shared/Api.fs for the DU half
// (`ApiCall`, `RemoteData`) and Client/Api.fs for the Fable-side
// `Api.makeProxy` helper.
//
// 0.4.1 — gained an optional `authContext` argument that bridges
// the forge-native auth attributes in `ToolUp.Platform.Core` to
// `ToolUp.Remoting.Server`'s Phase 69d `IAuthContext` resolver.
//
// 0.4.3 — closed the silent-no-op trap. If the API record `'T`
// carries any of the forge auth attributes but no `authContext`
// resolver was supplied, the dispatcher would silently skip
// enforcement. `Api.make` refused at composition with the
// offending member list.
//
// 0.4.4 (Phase 69b.tail) — wrapper auto-composes the Phase 69b
// platform seams default-on so consumers pick them up at the next
// package upgrade without code changes:
//   * Body normalisation — folded into the dispatcher itself at the
//     Phase 69b.A ship; `BodyNormalisation = Enabled` is the
//     `Remoting.createApi ()` default, so nothing to compose here.
//   * Correlation-id propagation + categorised error envelopes —
//     dispatcher-owned at the Phase 69b.D / 69b.E ship; same story.
//   * `IRemotingTelemetry` bridge over forge's registered
//     `IMetricsSink` — when the consumer omits `?telemetry`, the
//     wrapper installs a per-request resolver that bridges
//     `MethodTelemetry` to the registered `IMetricsSink` via
//     `Record("toolup.remoting.elapsed_ms", elapsedMs, …)`. With
//     `NoMetricsEndpoint` the sink resolves to `NoOpMetricsSink`
//     and the bridge becomes a true no-op (GP 13). The bridge uses
//     an `AsyncLocal<IServiceProvider>` captured from each request's
//     `HttpContext.RequestServices` (set in the wrapped api builder)
//     because `IRemotingTelemetry.OnMethodCompleted` does not
//     itself receive `HttpContext`.
//   * Default `ForgeAuthContext` resolver over Phase 66's `Subject`
//     — when the consumer omits `?authContext` AND the API record
//     carries forge auth attributes, the wrapper installs a default
//     resolver that reads `HttpContext.Items["ToolUp.Subject"]` /
//     `["ToolUp.User"]` (populated by `ScopeResolutionMiddleware`
//     via `ISubjectResolver`). The closure-captured Subject pattern
//     consumers used to write by hand collapses into this default,
//     and the 0.4.3 silent-no-op guard is no longer needed — the
//     default always enforces.

open System.Threading
open Microsoft.AspNetCore.Http
open ToolUp.Platform.Auth
open ToolUp.Platform.Metrics
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

/// Phase 69b.tail — internal seams for the `Api.make` wrapper's
/// default-on composition of the Phase 69b platform seams. Module is
/// `internal` to keep the public surface unchanged; the AsyncLocal
/// stash + default sinks are implementation detail.
module internal ApiSeams =
    /// Per-request `IServiceProvider` captured from the wrapped api
    /// builder's `HttpContext.RequestServices`. The default
    /// `IRemotingTelemetry` bridge reads from this AsyncLocal because
    /// `OnMethodCompleted` does not receive `HttpContext` directly.
    /// `IMetricsSink` is a Singleton so the AsyncLocal-resolved
    /// instance is identical across requests; thread-pool worker
    /// reuse between requests is therefore harmless for this seam.
    let requestServices = AsyncLocal<System.IServiceProvider>()

    /// Default `IRemotingTelemetry` that bridges per-method completion
    /// events to forge's registered `IMetricsSink`. No-op when no
    /// `IServiceProvider` has been stashed (test paths that bypass the
    /// wrapper) or when `IMetricsSink` resolves to `NoOpMetricsSink`
    /// (`NoMetricsEndpoint` deployments).
    let defaultMetricsBridge: IRemotingTelemetry =
        { new IRemotingTelemetry with
            member _.OnMethodCompleted t =
                match requestServices.Value with
                | null -> ()
                | services ->
                    match services.GetService(typeof<IMetricsSink>) with
                    | :? IMetricsSink as sink ->
                        let outcomeTag =
                            match t.Outcome with
                            | MethodOutcome.Succeeded -> "ok"
                            | MethodOutcome.Failed _ -> "error"

                        let tags = Map.ofList [ "method", t.MethodName; "outcome", outcomeTag ]

                        sink.Record("toolup.remoting.elapsed_ms", float t.ElapsedMs, tags)
                    | _ -> ()
        }

    /// Default `ForgeAuthContext` resolver reading Phase 66's
    /// `Subject` + `AuthenticatedUser` from `HttpContext.Items`
    /// (populated upstream by `ScopeResolutionMiddleware`). Pre
    /// Phase 69b.tail every forge consumer that wanted attribute
    /// enforcement wrote the same closure-captured resolver by hand;
    /// the default makes the wiring vanish.
    let defaultForgeAuthContextResolver (ctx: HttpContext) : Async<ForgeAuthContext> = async {
        let user =
            match ctx.Items.TryGetValue "ToolUp.User" with
            | true, (:? AuthenticatedUser as u) -> u
            | _ -> AuthenticatedUser.anonymous

        let subject =
            match ctx.Items.TryGetValue "ToolUp.Subject" with
            | true, (:? Subject as s) -> Some s
            | _ -> None

        let isAnonymousUser = AuthenticatedUser.isAnonymous user

        return
            { new ForgeAuthContext with
                member _.HasRole role = user.Roles |> List.contains role

                member _.HasClaim(claim, value) =
                    // Forge's first-party identity shape exposes
                    // `Email` and `TenantId` directly; bespoke claim
                    // consumers (e.g. custom JWT scopes) continue to
                    // wire their own resolver via `?authContext`.
                    match claim, value with
                    | "email", Some v -> user.Email = Some v
                    | "email", None -> user.Email.IsSome
                    | "tenantId", Some v -> user.TenantId = Some v
                    | "tenantId", None -> user.TenantId.IsSome
                    | _ -> false

                member _.HasTenant() =
                    match subject with
                    | Some(TeamMember _) -> true
                    | _ -> user.TenantId.IsSome

                member _.IsAnonymous() = isAnonymousUser

                member _.SubjectId =
                    match subject with
                    | Some(AnonymousSession sid) -> "anonymous:" + sid
                    | Some(Subject.AuthenticatedUser uid) -> "user:" + uid
                    | Some(TeamMember(uid, tid)) -> "team:" + tid + ":user:" + uid
                    | Some(Subject.ClaimBearer claim) -> "claim:" + claim.ScopeId
                    | None when isAnonymousUser -> "anonymous"
                    | None -> "user:" + user.UserId
            }
    }

    /// True when `'T` declares at least one forge auth attribute on a
    /// property or field. Used to decide whether the default
    /// `ForgeAuthContext` resolver should be composed when the consumer
    /// omits `?authContext`. Avoids paying the resolver's per-request
    /// cost on API records that don't need it.
    let typeHasForgeAuthAttrs (t: System.Type) : bool =
        let forgeAuthAttrFullNames =
            Set.ofList [
                typeof<RequiresRoleAttribute>.FullName
                typeof<RequiresClaimAttribute>.FullName
                typeof<TenantScopedAttribute>.FullName
                typeof<AllowAnonymousAttribute>.FullName
                typeof<PublicEndpointAttribute>.FullName
            ]

        let hasOnProperty =
            t.GetProperties()
            |> Array.exists (fun p ->
                p.GetCustomAttributes(false)
                |> Array.exists (fun a -> forgeAuthAttrFullNames.Contains(a.GetType().FullName)))

        let hasOnField =
            t.GetFields()
            |> Array.exists (fun f ->
                f.GetCustomAttributes(false)
                |> Array.exists (fun a -> forgeAuthAttrFullNames.Contains(a.GetType().FullName)))

        hasOnProperty || hasOnField

/// Server-side Fable Remoting helper. Mirrors SAFE.Api.make so server
/// call sites keep using `Api.make (builder, errorHandler = eh)`.
type Api =
    /// Build a Fable Remoting HttpHandler from an `HttpContext -> 'T`
    /// api builder. Matches SAFE.Api.make's signature: optional route
    /// builder, error handler, and remoting-options customiser.
    ///
    /// 0.4.1 — gained optional cross-cutting composers:
    ///
    /// * `authContext: HttpContext -> Async<ForgeAuthContext>` —
    ///   per-method auth-attribute enforcement. Omit to use the
    ///   default resolver, which reads `Subject` + `AuthenticatedUser`
    ///   from `HttpContext.Items` (populated by Phase 66's
    ///   `ScopeResolutionMiddleware`). Supply when bespoke claim
    ///   semantics or non-forge auth shape applies.
    ///
    /// * `telemetry: IRemotingTelemetry` — per-method telemetry
    ///   emission. Omit to use the default sink, which bridges to
    ///   forge's registered `IMetricsSink` via
    ///   `Record("toolup.remoting.elapsed_ms", …)`. Supply for custom
    ///   sinks (e.g. an OpenTelemetry-only path that doesn't go
    ///   through `IMetricsSink`).
    ///
    /// 0.4.4 (Phase 69b.tail) — both seams default to forge-wired
    /// behaviour. Source signature is unchanged: existing
    /// `Api.make (api, errorHandler = eh)` callers pick the seams up
    /// at the next package upgrade with no consumer code change.
    /// `NoMetricsEndpoint` deployments resolve `IMetricsSink` to
    /// `NoOpMetricsSink`, so the telemetry seam is genuinely zero-
    /// cost when metrics are off (GP 13). API records that don't
    /// declare any forge auth attributes skip the default
    /// `ForgeAuthContext` resolver entirely.
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

        // Phase 69b.tail — wrap the consumer's api builder so each
        // request stashes its `IServiceProvider` for the default
        // telemetry bridge to read. Composition with `Remoting.fromContext`
        // remains source-compat.
        let capturingApi (ctx: HttpContext) : 'T =
            ApiSeams.requestServices.Value <- ctx.RequestServices
            api ctx

        // Phase 69b.tail — resolve effective authContext. The default
        // resolver reads `HttpContext.Items["ToolUp.Subject"]` (Phase
        // 66) so consumers don't have to write the closure-captured
        // boilerplate. Skipped entirely when the type carries no forge
        // auth attributes — the dispatcher pays zero per-call cost in
        // that case.
        let effectiveAuthContext: (HttpContext -> Async<ForgeAuthContext>) option =
            match authContext with
            | Some resolver -> Some resolver
            | None ->
                if ApiSeams.typeHasForgeAuthAttrs typeof<'T> then
                    Some ApiSeams.defaultForgeAuthContextResolver
                else
                    None

        // Phase 69b.tail — resolve effective telemetry. The default
        // bridges to forge's `IMetricsSink`. With `NoMetricsEndpoint`
        // the resolved sink is `NoOpMetricsSink`, so the bridge's
        // `Record` call falls through to a no-op method — true zero-
        // cost path when metrics are off.
        let effectiveTelemetry: IRemotingTelemetry =
            match telemetry with
            | Some sink -> sink
            | None -> ApiSeams.defaultMetricsBridge

        // Bridge: ForgeAuthContext (declared on Platform.Core) →
        // IAuthContext (declared on ToolUp.Remoting.Server). Trivial
        // member-by-member adapter; isolates Platform.Core from a
        // ToolUp.Remoting.Server dependency.
        let bridgeAuth: (HttpContext -> Async<IAuthContext>) option =
            match effectiveAuthContext with
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
        |> Remoting.fromContext capturingApi
        |> (match errorHandler with
            | Some eh -> Remoting.withErrorHandler eh
            | None -> id)
        |> (match bridgeAuth with
            | Some resolver -> Remoting.withAuthContext resolver
            | None -> id)
        |> Remoting.withTelemetry effectiveTelemetry
        |> customOptions
        |> Remoting.buildHttpHandler
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
//
// 0.5.0 (Phase 69d.tail + 69h.tail) — the classifier validator goes
// DEFAULT-ON and dispatcher-emitted audit lights up:
//   * The default auth resolver is composed for EVERY F# record API
//     type, not just records that already carry auth attributes. The
//     dispatcher's startup classifier therefore refuses startup on any
//     record method lacking a `[<RequiresRole>]` / `[<RequiresClaim>]`
//     / `[<TenantScoped>]` / `[<AllowAnonymous>]` / `[<PublicEndpoint>]`
//     classification. BREAKING for consumers with unannotated records —
//     migration recipe at docs/migrations/69d-authorization-metadata.md.
//   * Records carrying `[<Audit "kind">]` annotations get a default
//     `IAuditEmitter` bridging dispatcher audit events to the
//     DI-registered `IAuditLog` as `RemotingMethodAudited` rows under
//     the request's config scope.
//   * `TOOLUP_AUDIT_ADMIN_REQUIRED=true` composes the admin-must-be-
//     audited startup gate: role-gated methods without `[<Audit>]`
//     refuse startup (compliance editions).

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

    /// Phase 69h.tail — per-request audit recording scope captured from
    /// the wrapped api builder (the request's `AccessContext` config
    /// scope, falling back to the caller's user id, then `_platform`).
    /// The default `IAuditEmitter` bridge reads from this AsyncLocal
    /// because `Emit` does not receive `HttpContext` directly — same
    /// pattern as `requestServices` above.
    let requestScopeId = AsyncLocal<string>()

    /// Phase 69l — process-wide memoised "is the registered
    /// `IMetricsSink` actually a real sink?" flag. Resolved on the
    /// first call where `requestServices.Value` is non-null. `IMetricsSink`
    /// is registered as a Singleton ([`ComposeRuntimeServices.fs`])
    /// so the cached value is correct for the process lifetime. A
    /// `false` cached value means "the registered sink is
    /// `NoOpMetricsSink` — the default-bridge can short-circuit
    /// upstream of any allocation". Used by `defaultBridgeGate` below.
    let mutable private metricsSinkActive: bool option = None
    let private metricsSinkActiveLock = obj ()

    /// Phase 69l — telemetry-allocation gate paired with
    /// `defaultMetricsBridge`. Returns `false` once the registered
    /// `IMetricsSink` is observed to be `NoOpMetricsSink`, telling the
    /// dispatcher to skip the per-request `Stopwatch` +
    /// `MethodTelemetry` allocation entirely. Until the first request
    /// has populated `requestServices.Value`, returns `true` (safe
    /// default — caller emits, the AsyncLocal-stashed sink falls
    /// through `NoOpMetricsSink.Record` exactly as pre-69l).
    let defaultBridgeGate () : bool =
        match metricsSinkActive with
        | Some v -> v
        | None ->
            match requestServices.Value with
            | null -> true
            | services ->
                lock metricsSinkActiveLock (fun () ->
                    match metricsSinkActive with
                    | Some v -> v
                    | None ->
                        let result =
                            match services.GetService(typeof<IMetricsSink>) with
                            | :? NoOpMetricsSink -> false
                            | _ -> true

                        metricsSinkActive <- Some result
                        result)

    /// Default `IRemotingTelemetry` that bridges per-method completion
    /// events to forge's registered `IMetricsSink`. No-op when no
    /// `IServiceProvider` has been stashed (test paths that bypass the
    /// wrapper) or when `IMetricsSink` resolves to `NoOpMetricsSink`
    /// (`NoMetricsEndpoint` deployments). Phase 69l: `Api.make` pairs
    /// this bridge with `defaultBridgeGate` so the per-request
    /// `Stopwatch` + `MethodTelemetry` allocation is gated upstream of
    /// `OnMethodCompleted` — `NoMetricsEndpoint` deployments pay
    /// nothing per request, fulfilling GP 13.
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
                            | MethodOutcome.RateLimited _ -> "rate-limited"

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
                member _.HasRole role =
                    // Phase 132 — the SDK's platform-admin grant is
                    // server-resolved into `ToolUp.PlatformRole` (Middleware
                    // Phase 4b, from `IPlatformAdminStore`), NOT the
                    // JWT-derived `user.Roles` list — which every shipped
                    // auth provider leaves empty. Without this bridge,
                    // `[<RequiresRole "PlatformAdmin">]` (on every shipped
                    // admin API record) evaluates `[].contains "PlatformAdmin"`
                    // → deny for *everyone*, including a genuine admin, so the
                    // attribute is dead enforcement and the handler-side
                    // `canModifyPlatformConfig` gate is the only real guard.
                    // Bridge the conventional "PlatformAdmin" role string to
                    // the server-resolved source of truth; any other role
                    // string falls through to `user.Roles` (consumer
                    // claim-mapper territory).
                    if role = "PlatformAdmin" then
                        match ctx.Items.TryGetValue "ToolUp.PlatformRole" with
                        | true, (:? PlatformRole as r) -> r = PlatformRole.PlatformAdmin
                        | _ -> false
                    else
                        user.Roles |> List.contains role

                member _.HasClaim(claim, value) =
                    // Forge's first-party identity shape exposes
                    // `Email` and `TenantId` directly; bespoke claim
                    // consumers (e.g. custom JWT scopes) continue to
                    // wire their own resolver via `?authContext`.
                    //
                    // Phase 69d.tail — `"scope"` is the forge-conventional
                    // claim for "any authenticated caller with a config
                    // scope" (`IConfigApi.SaveModuleConfig`-shaped methods:
                    // not role-gated, not tenant-only, but never anonymous).
                    // The `RequiresAuth` evaluation already denies anonymous
                    // callers before claims are consulted, so presence of a
                    // non-anonymous subject IS the scope claim.
                    //
                    // ⚠ NOT a per-scope / per-tenant binding. Reviewers
                    // must not read `[<RequiresClaim "scope">]` as "the
                    // caller owns *this* scope/tenant" — `HasClaim("scope",
                    // None)` resolves to exactly `not isAnonymousUser`, i.e.
                    // "any authenticated subject, including a share-token
                    // `ClaimBearer`". Tenant/scope ownership is enforced
                    // structurally by the `StorageScope` resolver (GP 4),
                    // never by this claim. A method that needs real
                    // per-tenant binding uses `[<TenantScoped>]`, not
                    // `[<RequiresClaim "scope">]`. (Auth-core audit, Authz
                    // Finding 6 — doc clarification, no behaviour change.)
                    match claim, value with
                    | "email", Some v -> user.Email = Some v
                    | "email", None -> user.Email.IsSome
                    | "tenantId", Some v -> user.TenantId = Some v
                    | "tenantId", None -> user.TenantId.IsSome
                    | "scope", None -> not isAnonymousUser
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

    /// Phase 69h.tail — default `IAuditEmitter` bridging dispatcher-
    /// emitted audit events to forge's DI-registered `IAuditLog` as
    /// `AuditEvent.RemotingMethodAudited` rows. No-op when no
    /// `IServiceProvider` has been stashed (test paths that bypass the
    /// wrapper) or when `IAuditLog` is unregistered. The recording scope
    /// comes from `requestScopeId` (stashed per request by the wrapped
    /// api builder), falling back to `_platform`.
    let defaultAuditEmitter: IAuditEmitter =
        { new IAuditEmitter with
            member _.Emit evt = async {
                match requestServices.Value with
                | null -> ()
                | services ->
                    match services.GetService(typeof<IAuditLog>) with
                    | :? IAuditLog as auditLog ->
                        let scopeId =
                            match requestScopeId.Value with
                            | null
                            | "" -> "_platform"
                            | s -> s

                        let kindName =
                            match evt.Kind with
                            | AuditKind.Custom name -> "Custom:" + name
                            | wellKnown -> string wellKnown

                        let payload: RemotingMethodAuditedPayload = {
                            Kind = kindName
                            MethodName = evt.MethodName
                            SubjectId = evt.SubjectId
                            CorrelationId = evt.CorrelationId
                            Payload = evt.Payload
                        }

                        do! auditLog.Record(scopeId, ToolUp.Platform.AuditEvent.RemotingMethodAudited payload)
                    | _ -> ()
            }
        }

    /// Phase 69h.tail — `TOOLUP_AUDIT_ADMIN_REQUIRED` env-var gate,
    /// read once per process. When truthy, `Api.make` composes
    /// `Remoting.withAuditRequiredOnRoleGated` so every role-gated
    /// method must also carry an `[<Audit>]` annotation or the
    /// dispatcher refuses startup (compliance-edition deployments).
    let auditAdminRequired =
        lazy
            (match System.Environment.GetEnvironmentVariable ConfigKeys.Names.auditAdminRequired with
             | null -> false
             | v ->
                 let v = v.Trim()
                 v.Equals("true", System.StringComparison.OrdinalIgnoreCase) || v = "1")

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
    /// API records that don't declare any forge auth attributes skip
    /// the default `ForgeAuthContext` resolver entirely.
    ///
    /// Phase 69l — telemetry seam is now genuinely zero-cost when
    /// metrics are off (GP 13). The default-bridge composition pairs
    /// the bridge with `ApiSeams.defaultBridgeGate`, which short-
    /// circuits per-request `Stopwatch` + `MethodTelemetry` allocation
    /// once it observes the registered `IMetricsSink` is
    /// `NoOpMetricsSink`. Consumer-supplied `?telemetry` sinks bypass
    /// the gate — the consumer opted in, so the dispatcher emits.
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
        // remains source-compat. Phase 69h.tail adds the audit recording
        // scope (the request's `AccessContext` config scope) for the
        // default audit emitter to read.
        let capturingApi (ctx: HttpContext) : 'T =
            ApiSeams.requestServices.Value <- ctx.RequestServices

            ApiSeams.requestScopeId.Value <-
                match ctx.RequestServices.GetService(typeof<AccessContext>) with
                | :? AccessContext as ac ->
                    ac
                    |> AccessContext.configScope
                    |> Option.map _.ScopeId
                    |> Option.defaultValue ac.UserId
                | _ -> "_platform"

            api ctx

        // Phase 69d.tail — the classifier validator is DEFAULT-ON. For
        // every F# record API type the wrapper composes an auth-context
        // resolver (the consumer's, or the default reading Phase 66's
        // `Subject` from `HttpContext.Items`), which arms the dispatcher's
        // startup classifier: any record method without one of
        // `[<RequiresRole>]` / `[<RequiresClaim>]` / `[<TenantScoped>]` /
        // `[<AllowAnonymous>]` / `[<PublicEndpoint>]` refuses startup with
        // a diagnostic naming the record + field. Pre-0.5.0 the resolver
        // was only composed when the record already carried at least one
        // attribute — which silently skipped enforcement for entirely
        // unannotated records, the exact "forgot the guard" defect class
        // Phase 69d exists to close. Non-record API types keep the
        // dormant pre-69d behaviour (record-field reflection cannot
        // classify them; the dispatcher refuses seam composition on
        // non-records anyway). Migration: docs/migrations/69d-authorization-metadata.md.
        // NonPublic flags — internal/private API records must arm the
        // classifier exactly like public ones; a bare `IsRecord` reports
        // false for them, which would silently skip the default-on
        // enforcement for precisely those records (fail-open). Same rule
        // as `AuthClassifier.reflectionFlags`.
        let typeIsRecord =
            Microsoft.FSharp.Reflection.FSharpType.IsRecord(typeof<'T>, AuthClassifier.reflectionFlags)

        let effectiveAuthContext: (HttpContext -> Async<ForgeAuthContext>) option =
            match authContext with
            | Some resolver -> Some resolver
            | None ->
                if typeIsRecord then
                    Some ApiSeams.defaultForgeAuthContextResolver
                else
                    None

        // Phase 132 — un-emittable-role startup warning. The default
        // `ForgeAuthContext` resolver only ever emits the server-resolved
        // `"PlatformAdmin"` role (bridged from `IPlatformAdminStore` /
        // `ToolUp.PlatformRole`); every other role string falls through to
        // the always-empty `user.Roles` of the first-party providers. So a
        // `[<RequiresRole "Editor">]` (any non-`PlatformAdmin` role) against
        // the default resolver is a dead gate — it denies *every* caller
        // with no compose-time signal, the exact trap Phase 132 closed for
        // `"PlatformAdmin"`, re-armed for every other role. Warn (don't
        // refuse — a consumer mid-migration may be about to wire a custom
        // resolver that emits the role) so the dead gate is visible at
        // startup rather than as a silent always-deny. Only fires for the
        // DEFAULT resolver: a consumer-supplied `?authContext` may emit any
        // role vocabulary, so its role requirements are not second-guessed.
        if authContext.IsNone && typeIsRecord then
            let deadRoles =
                AuthClassifier.unemittableRoles
                    (fun role -> role = "PlatformAdmin")
                    (AuthClassifier.classify typeof<'T>)

            if not (List.isEmpty deadRoles) then
                eprintfn
                    "[ToolUp.Remoting] WARN: API record '%s' has [<RequiresRole>] requirement(s) for role(s) [%s] that the default auth-context resolver can never emit — only \"PlatformAdmin\" is server-resolved (from IPlatformAdminStore). These gates deny EVERY caller (dead-gate). Supply a custom ?authContext resolver that emits the role, or do not use [<RequiresRole \"<role>\">] as a sole gate against the first-party providers."
                    typeof<'T>.Name
                    (String.concat "; " deadRoles)

        // Phase 69b.tail / 69l — resolve effective telemetry + gate.
        // The default bridge wires forge's `IMetricsSink` over
        // `IRemotingTelemetry`; Phase 69l pairs it with
        // `defaultBridgeGate` so the per-request `Stopwatch` +
        // `MethodTelemetry` allocation is skipped entirely when the
        // registered sink is `NoOpMetricsSink` (the `NoMetricsEndpoint`
        // default). Consumer-supplied sinks are NOT gated — the
        // consumer asked for telemetry, so the dispatcher emits.
        let effectiveTelemetry: IRemotingTelemetry =
            match telemetry with
            | Some sink -> sink
            | None -> ApiSeams.defaultMetricsBridge

        let telemetryIsDefault = telemetry.IsNone

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

        // Phase 69h.tail — default audit emitter. Composed only when the
        // record actually carries `[<Audit>]` annotations (zero cost for
        // unaudited records, GP 13); bridges dispatcher audit events to
        // the DI-registered `IAuditLog` as `RemotingMethodAudited` rows.
        let hasAuditedMethods =
            typeIsRecord && not (Map.isEmpty (Audit.classify typeof<'T>))

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
        |> (if telemetryIsDefault then
                Remoting.withTelemetryGate ApiSeams.defaultBridgeGate
            else
                id)
        |> (if hasAuditedMethods then
                Remoting.withAudit ApiSeams.defaultAuditEmitter
            else
                id)
        |> (if ApiSeams.auditAdminRequired.Value then
                Remoting.withAuditRequiredOnRoleGated
            else
                id)
        |> customOptions
        |> Remoting.buildHttpHandler
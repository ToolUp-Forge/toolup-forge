module ToolUp.Platform.DevDiagnosticsHandler

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.Auth
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.DataCatalog
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.ConfigValidatorAggregator

// ─── Dev diagnostics endpoint (Phase 9a) ─────────────────────────────
//
// Debug-only `/dev/inspect` endpoint that surfaces registered modules,
// the caller's resolved `AccessContext` / `StorageScope`, a snapshot of
// `IServiceCollection`'s descriptors (type names only), and the data
// catalog summary. Built to shorten "why doesn't my module appear?" /
// "why was this request denied?" debugging cycles.
//
// **Activation gate:** `ServerConfig.EnableDevEndpoints = true`. The
// flag's default `false` keeps the endpoint off in production
// deployments. The previous compile-time `#if DEBUG` belt-and-
// suspenders gate was removed when ToolUp.Platform stopped carrying
// compile-time gates; the runtime flag is now the sole gate.
//
// **Team-isolation contract.** The endpoint emits the *caller's* scope
// and access context only — no enumeration across teams, no cross-scope
// reads. Storage and event-store contents are never returned. The
// service list is type names only (no instances) so per-team singletons
// cannot leak through reflection.
//
// **Wire format.** Hand-shaped DTO of primitives, strings, and lists —
// no F# DUs round-trip through. Every DU on the report path is mapped
// to its case name as a string so `System.Text.Json` produces a clean,
// human-readable shape for `curl` / browser. No `FableConverters`
// dependency: this endpoint is for humans, not Fable.

// ─── Report DTO ──────────────────────────────────────────────────────

type ScopeSummary = {
    ScopeId: string
    Container: string
    Persist: bool
}

type CallerSummary = {
    UserId: string
    IsAnonymous: bool
    TeamId: string option
    Mode: string
    StorageScope: ScopeSummary option
    StorageScopeError: string option
    Permissions: Map<string, string list>
}

type ModuleSummary = {
    Name: string
    DataTypes: string list
    DataTypeCount: int
    HasConfigSchema: bool
}

type DataCatalogEntry = {
    Id: string
    DisplayName: string
    HasSchema: bool
    Producers: string list
}

type ServiceEntry = {
    ServiceType: string
    Lifetime: string
    Implementation: string option
}

/// One row in the "Lightweight profile" panel (Phase 1g). Lists every
/// gateable feature, the resolved mode (after `NotificationsAuto`
/// auto-detection has run), whether the feature is currently active in
/// this deployment, and the `ServerConfig` field a deployment uses to
/// change it. Sourced from the `DevDiagnosticsCapture`'s
/// `LightweightFeatures` field, populated at compose time.
type LightweightFeatureEntry = {
    Feature: string
    Mode: string
    Active: bool
    ConfigPath: string
}

/// One row in the "Health checks" panel (Phase 9k). Lists every
/// registered `IHealthCheck` with its current outcome — operators
/// reading `/dev/inspect` see the same probe results that back
/// `/ready` without making a second HTTP round-trip. Probes are
/// invoked in parallel with their declared per-probe timeout, mirroring
/// the BCL aggregator's behaviour.
type HealthCheckSummary = {
    Name: string
    Kind: string // "Liveness" | "Readiness"
    TimeoutMs: int
    Status: string // "Healthy" | "Degraded" | "Unhealthy"
    Message: string
}

/// One row in the "Config preflight" panel (Phase 9m). Surfaces the
/// snapshot captured at compose end — operators see whether the
/// deployment passed startup-time preflight without grepping the
/// startup log. Snapshot, not live re-run: validators are heavier
/// than health probes (sentinel writes, DNS resolution) and re-running
/// them on every dev-page hit would amplify side effects.
type ValidatorSummary = {
    Name: string
    Status: string // "Ok" | "Warning" | "Error"
    Message: string
    ElapsedMs: int64
}

/// Phase 1f composition-seam summary. Surfaces the hook counts, the
/// CORS state, and the configured `SecurityHeaders` keys (values
/// redacted) so debug builds can see what the consumer wired through
/// the seam. No raw header values — a CSP / `Permissions-Policy`
/// payload may carry deployment-private data.
type CompositionSeamSummary = {
    PreMiddlewareCount: int
    PostMiddlewareCount: int
    SecurityHeaderKeys: string list
    CorsConfigured: bool
    NotificationConsumers: string list
}

/// Phase 16a — one row in the "Process profile" panel. Per-subsystem
/// gating outcome: whether `compose` registered the
/// `IHostedService`, and a one-line reason citing which combination
/// of `ServerlessHost` + `ProcessProfile` produced the outcome.
/// Mirrors the matrix in `Server/Compose/ProcessProfileGate.fs` so
/// operators see "this silo is `WorkerOnly`; the web tier mounts at
/// `/api/*`" without reading source.
type ProcessProfileSubsystemEntry = {
    Name: string
    Registered: bool
    Reason: string
}

/// Phase 16a — process profile panel. Surfaces the active
/// `ProcessProfile` + `ServerlessHost` selection, whether the HTTP
/// pipeline is mounted in this silo, and the per-subsystem gating
/// outcome derived from `ProcessProfileGate`. Source of truth for
/// "what is this silo doing?" diagnostics in a multi-silo deployment.
/// See `technical-guide/13-deployment-shapes.md` for the three
/// pure-Kestrel shapes and the cross-silo coordination contract.
type ProcessProfileSummary = {
    Profile: string
    ServerlessHost: string
    HttpPipelineMounted: bool
    Subsystems: ProcessProfileSubsystemEntry list
}

type DevDiagnosticsReport = {
    Generated: string
    BuildMode: string
    Surfaces: string
    Caller: CallerSummary
    Modules: ModuleSummary list
    TotalRouteHandlers: int
    DataCatalog: DataCatalogEntry list
    Services: ServiceEntry list
    /// Per-index drift snapshot (Phase 9f Step 5). One entry per
    /// indexed store × index. Empty when no indexed stores are
    /// registered. Sampled for the caller's resolved scope only —
    /// never enumerates across teams.
    IndexConsistency: SecondaryIndex.IndexConsistencyEntry list
    /// Phase 1g — lightweight composition profile. Lists every
    /// gateable feature, its resolved mode, and whether it's currently
    /// active. Source of truth for "what's running in this
    /// deployment?" diagnostics; future phases use it to confirm GP 13
    /// compliance without trawling DI dumps.
    LightweightFeatures: LightweightFeatureEntry list
    /// Phase 1f — composition-seam summary. Hook counts and the
    /// configured `SecurityHeaders` keys (values redacted) so the
    /// dev panel shows what the consumer wired through
    /// `withPreMiddleware` / `withPostMiddleware` /
    /// `ServerConfig.SecurityHeaders` / `ServerConfig.Cors`.
    CompositionSeam: CompositionSeamSummary
    /// Phase 9k — per-probe health-check status. Empty when no
    /// probes are registered; otherwise one entry per `IHealthCheck`
    /// resolved from the caller's `HttpContext.RequestServices`.
    /// Probes are invoked in parallel with their declared timeout,
    /// mirroring the BCL aggregator behind `/ready`.
    HealthChecks: HealthCheckSummary list
    /// Phase 9m — config preflight outcomes from the most recent
    /// startup. Empty when no validators are registered or
    /// `ServerConfig.SkipPreflight = true`. Snapshot captured at
    /// end-of-compose; this endpoint does NOT re-run validators on
    /// each request (heavier than health probes — sentinel writes,
    /// remote handshakes — re-running on every dev-page hit would
    /// amplify side effects).
    Validators: ValidatorSummary list

    /// Phase 6h follow-up — Workstream B. Open extension surface for
    /// `IDevDiagnosticsContributor` registrations. Each entry is one
    /// contributor's `(panelName, payload)` return; `payload` is
    /// already serialised to a JSON string so the wire DTO is
    /// uniformly shaped. Empty when no contributors are registered.
    /// See [src/ToolUp.Platform/Shared/IDevDiagnosticsContributor.fs]
    /// for the contributor contract.
    Contributors: Map<string, string>

    /// Phase 16a tail — process-profile panel. Reports the resolved
    /// `ProcessProfile` × `ServerlessHost` matrix decisions for the
    /// running silo: whether HTTP is mounted here, and which
    /// background subsystems registered. Empty `Subsystems` is
    /// impossible — the eight subsystem rows are constants. Appended
    /// at the end of the report so existing JSON consumers continue
    /// to find every field they expect.
    ProcessProfile: ProcessProfileSummary
}

// ─── Compose-time captures ───────────────────────────────────────────

/// Per-module metadata captured at compose time. Carries enough to
/// answer "what did this module register?" without reaching back into
/// the `ServerModule` record at request time.
type ModuleSnapshot = {
    Name: string
    DataTypeIds: string list
    HasConfigSchema: bool
}

/// Service-collection descriptor snapshot. Captured before
/// `WebApplication.CreateBuilder().Build()` so the dev endpoint can
/// list every registered service without holding a live reference to
/// the collection (which becomes immutable post-`Build()`).
type ServiceDescriptorSnapshot = {
    ServiceTypeName: string
    Lifetime: string
    ImplementationTypeName: string option
}

/// Compose-time inputs the dev handler needs at request time. Captured
/// once and closed over by the route handler.
type DevDiagnosticsCapture = {
    Modules: ModuleSnapshot list
    Services: ServiceDescriptorSnapshot list
    TotalRouteHandlers: int
    /// Per-store inspectors that produce `IndexConsistencyEntry` lists
    /// for the caller's scope. Closed over the concrete store
    /// instance at compose time so the handler doesn't need to
    /// service-locate. Empty when no indexed store is registered.
    IndexInspectors: (string -> Async<SecondaryIndex.IndexConsistencyEntry list>) list
    /// Phase 1g — composition audit. One entry per gateable feature
    /// with the resolved mode (after auto-detection) and whether it's
    /// active in this deployment. Built by `compose` and surfaced via
    /// `/dev/inspect`'s "Lightweight profile" panel.
    LightweightFeatures: LightweightFeatureEntry list
    /// Phase 1f — composition-seam summary captured at compose time.
    /// Hook counts (pre/post middleware), `SecurityHeaders` keys
    /// (values redacted), CORS active flag.
    CompositionSeam: CompositionSeamSummary
}

// ─── Snapshot helpers ────────────────────────────────────────────────

let private lifetimeName (lifetime: ServiceLifetime) =
    match lifetime with
    | ServiceLifetime.Singleton -> "Singleton"
    | ServiceLifetime.Scoped -> "Scoped"
    | ServiceLifetime.Transient -> "Transient"
    | _ -> string lifetime

let private typeFullName (t: Type) =
    if isNull t then
        None
    else
        Some(if isNull t.FullName then t.Name else t.FullName)

/// Snapshot every `ServiceDescriptor` currently in the collection.
/// Order preserved so the dev list mirrors registration order, which
/// is itself a useful debugging cue (last-registered-wins for keyed
/// duplicates).
let snapshotServices (services: IServiceCollection) : ServiceDescriptorSnapshot list =
    services
    |> Seq.map (fun d ->
        let implTypeName =
            match typeFullName d.ImplementationType with
            | Some n -> Some n
            | None ->
                if not (isNull d.ImplementationInstance) then
                    typeFullName (d.ImplementationInstance.GetType())
                elif not (isNull d.ImplementationFactory) then
                    Some "<factory>"
                else
                    None

        {
            ServiceTypeName = typeFullName d.ServiceType |> Option.defaultValue "<unknown>"
            Lifetime = lifetimeName d.Lifetime
            ImplementationTypeName = implTypeName
        })
    |> List.ofSeq

// ─── Per-request builders ────────────────────────────────────────────

let private permissionName (perm: ModulePermission) =
    match perm with
    | ModulePermission.Read -> "Read"
    | ModulePermission.Write -> "Write"
    | ModulePermission.Admin -> "Admin"

/// Runtime detection of whether the entry assembly was compiled with
/// JIT optimisations disabled (the `Debug` configuration). Replaces the
/// previous compile-time `#if DEBUG` gate; this value reflects how the
/// hosting App was built, not how ToolUp.Platform itself was built.
let private buildMode =
    let entry = System.Reflection.Assembly.GetEntryAssembly()

    if isNull entry then
        "Unknown"
    else
        let attrs =
            entry.GetCustomAttributes(typeof<System.Diagnostics.DebuggableAttribute>, false)

        if isNull attrs || attrs.Length = 0 then
            "Release"
        else
            let attr = attrs[0] :?> System.Diagnostics.DebuggableAttribute

            if attr.IsJITOptimizerDisabled then "Debug" else "Release"

/// Resolve the caller's identity + scope manually — the dev endpoint
/// lives outside `/api/*` so `ScopeResolutionMiddleware` only runs for
/// it when the middleware was widened to include `/dev/*` (which the
/// SDK does in `compose`). Falls back to anonymous on any error so the
/// dev endpoint is itself usable while you're diagnosing auth failures.
let private buildCallerSummary (ctx: HttpContext) (config: ServerConfig) = async {
    let authProv =
        ctx.RequestServices.GetService(typeof<IAuthProvider>) :?> IAuthProvider

    let resolver =
        ctx.RequestServices.GetService(typeof<IStorageScopeResolver>) :?> IStorageScopeResolver

    let! request = async {
        try
            return! ScopeRequestExtractor.fromHttpContext ctx authProv
        with _ ->
            return
                ({
                    User = Some AuthenticatedUser.anonymous
                    SessionId = None
                    Headers = Map.empty
                }
                : ScopeResolutionRequest)
    }

    let user = request.User |> Option.defaultValue AuthenticatedUser.anonymous

    let! scopeResult = async {
        try
            let! r = resolver.Resolve request
            return Some r
        with _ ->
            return None
    }

    let scopeSummary, scopeError =
        match scopeResult with
        | Some(Ok scope) ->
            Some {
                ScopeId = scope.ScopeId
                Container = scope.Container
                Persist = scope.Persist
            },
            None
        | Some(Error err) -> None, Some(string err)
        | None -> None, Some "scope resolver threw — see server logs"

    let teamId =
        match scopeSummary with
        | Some s when s.Container.StartsWith "team-" -> Some s.ScopeId
        | _ -> None

    // AccessContext is a Scoped DI service, but its factory reads from
    // `HttpContext.Items` populated by `ScopeResolutionMiddleware`. We
    // resolve via DI so the dev report mirrors what API handlers see
    // for this same request.
    let accessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac
        | _ ->
            // Defensive fallback when the Scoped AccessContext is absent
            // (no ScopeResolutionMiddleware in the pipeline). Reconstruct
            // the caller's subject from the resolved identity + the
            // deployment surfaces — team-scoped + a resolved team →
            // `TeamMember`; any authenticated surface → `AuthenticatedUser`;
            // anonymous-only → `AnonymousSession`. Mirrors the dominant-
            // surface mapping a real request would have produced.
            let fallbackSubject =
                match teamId with
                | Some tid when DeploymentConfig.hasTeamScope config -> TeamMember(user.UserId, tid)
                | _ when DeploymentConfig.requiresAnyAuth config -> AuthenticatedUser user.UserId
                | _ -> AnonymousSession user.UserId

            AccessContext.unrestricted fallbackSubject

    let permissions =
        accessContext.ModulePermissions
        |> Map.map (fun _ perms -> perms |> List.map permissionName)

    return {
        UserId = user.UserId
        IsAnonymous = AuthenticatedUser.isAnonymous user
        TeamId = accessContext.TeamId |> Option.orElse teamId
        Mode = DeploymentConfig.surfacesLabel config
        StorageScope = scopeSummary
        StorageScopeError = scopeError
        Permissions = permissions
    }
}

/// Build the per-probe health summary by resolving every registered
/// `IHealthCheck` and invoking it via the shared
/// `HealthCheckRunner.runOne`. Mirrors the aggregator's behaviour but
/// goes around BCL — the dev report stays usable when the BCL pipeline
/// is misconfigured. The summary DTO drops `ElapsedMs` (carried by
/// `ProbeRun`) because the dev panel doesn't surface it; the
/// production-safe `HealthMonitorApi` does.
let private buildHealthChecks (ctx: HttpContext) = async {
    let probes = ctx.RequestServices.GetServices<IHealthCheck>() |> List.ofSeq

    if probes.IsEmpty then
        return []
    else
        let! runs = probes |> List.map HealthCheckRunner.runOne |> Async.Parallel

        return
            runs
            |> Array.map (fun r -> {
                Name = r.Name
                Kind = HealthKind.toString r.Kind
                TimeoutMs = r.TimeoutMs
                Status = r.Status
                Message = r.Message
            })
            |> Array.toList
}

/// Build the Phase 9m validators panel by resolving the
/// `IPreflightSnapshot` singleton from DI and translating every
/// recorded outcome into a wire-format row. Returns `[]` when the
/// snapshot service isn't registered (older deployments composed
/// before Phase 9m landed) or when the snapshot is empty (no
/// validators registered, or `SkipPreflight = true`).
let private buildValidators (ctx: HttpContext) : ValidatorSummary list =
    match ctx.RequestServices.GetService(typeof<IPreflightSnapshot>) with
    | :? IPreflightSnapshot as snap ->
        snap.LastRun
        |> List.map (fun o -> {
            Name = o.Name
            Status = ConfigValidation.ValidationResult.status o.Result
            Message = ConfigValidation.ValidationResult.message o.Result
            ElapsedMs = o.ElapsedMs
        })
    | _ -> []

/// Build the data-catalog summary by resolving `IDataCatalog` from DI
/// and walking its registered types. Producers list is the canonical
/// source for "which module produced this type?" — the dev endpoint
/// echoes it verbatim.
let private buildDataCatalog (ctx: HttpContext) = async {
    let catalog =
        match ctx.RequestServices.GetService(typeof<IDataCatalog>) with
        | :? IDataCatalog as c -> Some c
        | _ -> None

    match catalog with
    | None -> return []
    | Some c ->
        let! types = c.ListTypes()

        let! entries =
            types
            |> List.map (fun info -> async {
                let! producers = c.GetProducers(info.Id)

                return {
                    Id = info.Id
                    DisplayName = info.DisplayName
                    HasSchema = info.Schema.IsSome
                    Producers = producers
                }
            })
            |> Async.Sequential

        return entries |> List.ofArray
}

/// Phase 6h follow-up — Workstream B. Resolve every DI-registered
/// `IDevDiagnosticsContributor`, await each one's `Contribute()`, and
/// JSON-serialise the returned payload. Failures in individual
/// contributors are isolated — one slow / throwing contributor cannot
/// block the rest of the report. Order is non-deterministic
/// (`GetServices<T>()` ordering is implementation-defined); the report
/// returns a `Map` so HTML rendering can be alphabetical.
let private buildContributors (ctx: HttpContext) : Async<Map<string, string>> = async {
    let contributors =
        ctx.RequestServices.GetServices(typeof<IDevDiagnosticsContributor>)
        |> Seq.cast<IDevDiagnosticsContributor>
        |> Seq.toList

    if contributors.IsEmpty then
        return Map.empty
    else
        // Bound each contributor to 2s so a wedged one doesn't drag
        // the whole /dev/inspect page. On timeout or throw, record
        // a placeholder so the panel still surfaces something.
        let safeContribute (c: IDevDiagnosticsContributor) : Async<(string * string) option> = async {
            try
                let workTask = c.Contribute() |> Async.StartAsTask

                let timeoutTask = System.Threading.Tasks.Task.Delay(2000)

                let! winner =
                    System.Threading.Tasks.Task.WhenAny(workTask :> System.Threading.Tasks.Task, timeoutTask)
                    |> Async.AwaitTask

                if winner = (workTask :> System.Threading.Tasks.Task) then
                    let! (name, payload) = workTask |> Async.AwaitTask

                    let json = JsonSerializer.Serialize(payload, FableConverters.create ())

                    return Some(name, json)
                else
                    return Some(c.GetType().Name, "\"<contributor timed out after 2s>\"")
            with ex ->
                return Some(c.GetType().Name, JsonSerializer.Serialize(sprintf "<contributor threw: %s>" ex.Message))
        }

        let! results = contributors |> List.map safeContribute |> Async.Parallel

        return results |> Array.toList |> List.choose id |> Map.ofList
}

/// Phase 16a tail — derive the per-silo `ProcessProfile` panel from
/// `config`. Pure function over the config record; no DI, no I/O.
/// Subsystem display order mirrors the `BackgroundSubsystem` DU
/// declaration order in `ProcessProfileGate.fs` so the table reads
/// "scheduler / dispatchers / drains / sweeps" top-down.
let private buildProcessProfile (config: ServerConfig) : ProcessProfileSummary =
    let serverlessName =
        match config.ServerlessHost with
        | KestrelHost -> "KestrelHost"
        | ServerlessHost -> "ServerlessHost"

    let profileName =
        match config.ProcessProfile with
        | AllInOne -> "AllInOne"
        | WebOnly -> "WebOnly"
        | WorkerOnly -> "WorkerOnly"
        | DispatcherOnly -> "DispatcherOnly"

    let reasonFor (registered: bool) =
        match config.ServerlessHost, config.ProcessProfile with
        | ServerlessHost, _ -> "ServerlessHost short-circuits every background subsystem"
        | KestrelHost, AllInOne -> "AllInOne runs every background subsystem"
        | KestrelHost, WebOnly -> "WebOnly skips every background subsystem (sibling WorkerOnly drains)"
        | KestrelHost, WorkerOnly -> "WorkerOnly runs every background subsystem"
        | KestrelHost, DispatcherOnly ->
            if registered then
                "DispatcherOnly runs only the outbound dispatchers"
            else
                "DispatcherOnly skips this subsystem (outbound-delivery isolation)"

    let subsystems =
        [
            JobSchedulerSubsystem, "Job scheduler"
            WebhookDispatcherSubsystem, "Webhook dispatcher"
            TransactionalDispatcherSubsystem, "Transactional dispatcher"
            AuditReplicatorSubsystem, "Audit replicator"
            UsageBatchFlusherSubsystem, "Usage batch flusher"
            HealthStateTrackerSubsystem, "Health-state tracker"
            OAuthStateCleanupSubsystem, "OAuth state-store cleanup"
            OAuthRefresherRecoverSubsystem, "OAuth refresher startup-Recover"
        ]
        |> List.map (fun (subsystem, displayName) ->
            let registered = ProcessProfileGate.shouldRegisterBackgroundService config subsystem

            {
                Name = displayName
                Registered = registered
                Reason = reasonFor registered
            })

    {
        Profile = profileName
        ServerlessHost = serverlessName
        HttpPipelineMounted = ProcessProfileGate.shouldRegisterHttpPipeline config
        Subsystems = subsystems
    }

let buildReport
    (config: ServerConfig)
    (capture: DevDiagnosticsCapture)
    (ctx: HttpContext)
    : Async<DevDiagnosticsReport> =
    async {
        let! caller = buildCallerSummary ctx config
        let! catalog = buildDataCatalog ctx
        let! healthChecks = buildHealthChecks ctx
        let validators = buildValidators ctx
        let! contributors = buildContributors ctx
        let processProfile = buildProcessProfile config

        let modules =
            capture.Modules
            |> List.map (fun m -> {
                Name = m.Name
                DataTypes = m.DataTypeIds
                DataTypeCount = m.DataTypeIds.Length
                HasConfigSchema = m.HasConfigSchema
            })

        let services =
            capture.Services
            |> List.map (fun s -> {
                ServiceType = s.ServiceTypeName
                Lifetime = s.Lifetime
                Implementation = s.ImplementationTypeName
            })

        // Index consistency — sampled against the caller's scope
        // only (GP4 — never enumerate across teams). When the caller
        // has no resolved scope we skip — the report can still be
        // useful without it.
        let! indexConsistency =
            match caller.StorageScope with
            | Some s when capture.IndexInspectors.Length > 0 -> async {
                let! perInspector =
                    capture.IndexInspectors
                    |> List.map (fun inspect -> async {
                        try
                            return! inspect s.ScopeId
                        with _ ->
                            return []
                    })
                    |> Async.Parallel

                return perInspector |> Array.toList |> List.collect id
              }
            | _ -> async.Return []

        return {
            Generated = DateTime.UtcNow.ToString("o")
            BuildMode = buildMode
            Surfaces = DeploymentConfig.surfacesLabel config
            Caller = caller
            Modules = modules
            TotalRouteHandlers = capture.TotalRouteHandlers
            DataCatalog = catalog
            Services = services
            IndexConsistency = indexConsistency
            LightweightFeatures = capture.LightweightFeatures
            CompositionSeam = capture.CompositionSeam
            HealthChecks = healthChecks
            Validators = validators
            Contributors = contributors
            ProcessProfile = processProfile
        }
    }

// ─── Renderers ───────────────────────────────────────────────────────

// Canonical SDK JSON helper — `FableConverters` round-trips F#
// records, `option`, and `Map<string,_>` losslessly. The dev endpoint
// has no DUs on the wire (every DU on the report path is pre-mapped
// to a case-name string), so the output stays human-friendly.
let private jsonOptions =
    let o = FableConverters.create ()
    o.WriteIndented <- true
    o

let private renderJson (report: DevDiagnosticsReport) : string =
    JsonSerializer.Serialize(report, jsonOptions)

/// Minimal HTML view — one section per top-level field, plain `<pre>`
/// + `<table>` for browser-friendly eyeballing. Deliberately no CSS
/// framework / no JS: a curl-fetch-and-pipe-through-w3m workflow keeps
/// working, and there's no XSS surface beyond what HtmlEncode covers.
let private encode (s: string) = System.Net.WebUtility.HtmlEncode s

let private mutedDash = """<span class="muted">—</span>"""
let private okTick = """<span class="ok">✓</span>"""

let private mark (b: bool) = if b then okTick else mutedDash

let private renderHtml (report: DevDiagnosticsReport) : string =
    let sb = StringBuilder()
    sb.AppendLine "<!DOCTYPE html>" |> ignore

    sb.AppendLine """<html><head><meta charset="utf-8"><title>ToolUp /dev/inspect</title>"""
    |> ignore

    sb.AppendLine
        """<style>body{font-family:system-ui,-apple-system,sans-serif;max-width:1100px;margin:1em auto;padding:0 1em;color:#222}h1{margin-top:0}h2{border-bottom:1px solid #ccc;padding-bottom:.2em;margin-top:1.5em}table{border-collapse:collapse;width:100%;font-size:.9em}th,td{border:1px solid #ddd;padding:.3em .5em;text-align:left;vertical-align:top}th{background:#f4f4f4}code{background:#f4f4f4;padding:.1em .3em;border-radius:3px}.muted{color:#888}.ok{color:#127c12}.err{color:#a82020}</style>"""
    |> ignore

    sb.AppendLine "</head><body>" |> ignore
    sb.AppendLine """<h1>ToolUp <code>/dev/inspect</code></h1>""" |> ignore

    let header =
        sprintf
            """<p class="muted">Generated %s · build %s · surfaces %s</p>"""
            (encode report.Generated)
            (encode report.BuildMode)
            (encode report.Surfaces)

    sb.AppendLine header |> ignore

    // Caller section
    sb.AppendLine "<h2>Caller</h2><table>" |> ignore

    let anonymousMarker =
        if report.Caller.IsAnonymous then
            """ <span class="muted">(anonymous)</span>"""
        else
            ""

    sb.AppendLine(
        sprintf "<tr><th>UserId</th><td><code>%s</code>%s</td></tr>" (encode report.Caller.UserId) anonymousMarker
    )
    |> ignore

    let teamCell =
        report.Caller.TeamId
        |> Option.map encode
        |> Option.defaultValue (sprintf """<span class="muted">none</span>""")

    sb.AppendLine(sprintf "<tr><th>TeamId</th><td>%s</td></tr>" teamCell) |> ignore

    sb.AppendLine(sprintf "<tr><th>Mode</th><td>%s</td></tr>" (encode report.Caller.Mode))
    |> ignore

    match report.Caller.StorageScope with
    | Some s ->
        let persistText = if s.Persist then "true" else "false"

        sb.AppendLine(
            sprintf
                "<tr><th>StorageScope</th><td>ScopeId=<code>%s</code> · Container=<code>%s</code> · Persist=%s</td></tr>"
                (encode s.ScopeId)
                (encode s.Container)
                persistText
        )
        |> ignore
    | None ->
        let err =
            report.Caller.StorageScopeError
            |> Option.map encode
            |> Option.defaultValue "no scope resolved"

        sb.AppendLine(sprintf """<tr><th>StorageScope</th><td><span class="err">%s</span></td></tr>""" err)
        |> ignore

    if report.Caller.Permissions.IsEmpty then
        sb.AppendLine
            """<tr><th>Permissions</th><td><span class="muted">empty map → unrestricted (every module accessible)</span></td></tr>"""
        |> ignore
    else
        let rows =
            report.Caller.Permissions
            |> Map.toList
            |> List.map (fun (k, v) ->
                let perms = v |> List.map encode |> String.concat ", "
                sprintf "<code>%s</code>: %s" (encode k) perms)
            |> String.concat "<br>"

        sb.AppendLine(sprintf "<tr><th>Permissions</th><td>%s</td></tr>" rows) |> ignore

    sb.AppendLine "</table>" |> ignore

    // Process profile (Phase 16a tail) — surfaces the resolved
    // ProcessProfile × ServerlessHost matrix decisions for this silo.
    // Placed high in the page so an operator inspecting a multi-silo
    // deployment confirms "this is the worker, the web tier mounts
    // /api/* elsewhere" before reading anything else.
    sb.AppendLine "<h2>Process profile</h2>" |> ignore

    let pipelineCell =
        if report.ProcessProfile.HttpPipelineMounted then
            okTick
        else
            sprintf """<span class="muted">not mounted</span>"""

    sb.AppendLine "<table>" |> ignore

    sb.AppendLine(sprintf "<tr><th>Profile</th><td><code>%s</code></td></tr>" (encode report.ProcessProfile.Profile))
    |> ignore

    sb.AppendLine(
        sprintf
            "<tr><th>Serverless host</th><td><code>%s</code></td></tr>"
            (encode report.ProcessProfile.ServerlessHost)
    )
    |> ignore

    sb.AppendLine(sprintf "<tr><th>HTTP pipeline</th><td>%s</td></tr>" pipelineCell)
    |> ignore

    sb.AppendLine "</table>" |> ignore

    sb.AppendLine
        """<p class="muted">See <code>technical-guide/13-deployment-shapes.md</code> for the three pure-Kestrel deployment shapes and the cross-silo coordination contract. The gating matrix lives in <code>Server/Compose/ProcessProfileGate.fs</code>.</p>"""
    |> ignore

    sb.AppendLine "<table><tr><th>Subsystem</th><th>Registered</th><th>Reason</th></tr>"
    |> ignore

    for s in report.ProcessProfile.Subsystems do
        sb.AppendLine(
            sprintf "<tr><td>%s</td><td>%s</td><td>%s</td></tr>" (encode s.Name) (mark s.Registered) (encode s.Reason)
        )
        |> ignore

    sb.AppendLine "</table>" |> ignore

    // Modules section
    sb.AppendLine(sprintf "<h2>Modules (%d)</h2>" report.Modules.Length) |> ignore

    sb.AppendLine(sprintf """<p class="muted">Total registered route handlers: %d</p>""" report.TotalRouteHandlers)
    |> ignore

    if report.Modules.IsEmpty then
        sb.AppendLine """<p class="muted">No modules registered.</p>""" |> ignore
    else
        sb.AppendLine "<table><tr><th>Name</th><th>Data types</th><th>Config schema</th></tr>"
        |> ignore

        for m in report.Modules do
            let dataTypesCell =
                if m.DataTypes.IsEmpty then
                    mutedDash
                else
                    m.DataTypes
                    |> List.map (fun id -> sprintf "<code>%s</code>" (encode id))
                    |> String.concat ", "

            sb.AppendLine(
                sprintf
                    "<tr><td><code>%s</code></td><td>%s</td><td>%s</td></tr>"
                    (encode m.Name)
                    dataTypesCell
                    (mark m.HasConfigSchema)
            )
            |> ignore

        sb.AppendLine "</table>" |> ignore

    // Data catalog
    sb.AppendLine(sprintf "<h2>Data catalog (%d)</h2>" report.DataCatalog.Length)
    |> ignore

    if report.DataCatalog.IsEmpty then
        sb.AppendLine """<p class="muted">No data types registered.</p>""" |> ignore
    else
        sb.AppendLine "<table><tr><th>Id</th><th>Display name</th><th>Schema</th><th>Producers</th></tr>"
        |> ignore

        for d in report.DataCatalog do
            let producers =
                if d.Producers.IsEmpty then
                    mutedDash
                else
                    d.Producers |> List.map encode |> String.concat ", "

            sb.AppendLine(
                sprintf
                    "<tr><td><code>%s</code></td><td>%s</td><td>%s</td><td>%s</td></tr>"
                    (encode d.Id)
                    (encode d.DisplayName)
                    (mark d.HasSchema)
                    producers
            )
            |> ignore

        sb.AppendLine "</table>" |> ignore

    // Index consistency (Phase 9f Step 5)
    sb.AppendLine(sprintf "<h2>Index consistency (%d)</h2>" report.IndexConsistency.Length)
    |> ignore

    if report.IndexConsistency.IsEmpty then
        sb.AppendLine """<p class="muted">No indexed stores registered, or caller has no resolved scope.</p>"""
        |> ignore
    else
        sb.AppendLine
            """<p class="muted">Sampled for the caller's scope only. Drift > 0 in Orphans or Unindexed columns flags a recoverable bug class — see <code>IMaintenanceApi.Rebuild*</code>.</p>"""
        |> ignore

        sb.AppendLine
            "<table><tr><th>Store</th><th>Index</th><th>Sample</th><th>Consistent</th><th>Orphans</th><th>Unindexed</th></tr>"
        |> ignore

        let tagDrift (n: int) =
            if n = 0 then
                string n
            else
                sprintf """<span class="err">%d</span>""" n

        for e in report.IndexConsistency do
            sb.AppendLine(
                sprintf
                    "<tr><td><code>%s</code></td><td><code>%s</code></td><td>%d</td><td>%d</td><td>%s</td><td>%s</td></tr>"
                    (encode e.StoreName)
                    (encode e.IndexName)
                    e.SampleSize
                    e.ConsistentEntries
                    (tagDrift e.OrphanedIndexEntries)
                    (tagDrift e.UnindexedCanonicals)
            )
            |> ignore

        sb.AppendLine "</table>" |> ignore

    // Composition seam (Phase 1f)
    sb.AppendLine "<h2>Composition seam</h2>" |> ignore
    sb.AppendLine "<table>" |> ignore

    sb.AppendLine(sprintf "<tr><th>Pre-middleware hooks</th><td>%d</td></tr>" report.CompositionSeam.PreMiddlewareCount)
    |> ignore

    sb.AppendLine(
        sprintf "<tr><th>Post-middleware hooks</th><td>%d</td></tr>" report.CompositionSeam.PostMiddlewareCount
    )
    |> ignore

    sb.AppendLine(
        sprintf
            "<tr><th>CORS</th><td>%s</td></tr>"
            (if report.CompositionSeam.CorsConfigured then
                 okTick
             else
                 mutedDash)
    )
    |> ignore

    let headerKeysCell =
        if report.CompositionSeam.SecurityHeaderKeys.IsEmpty then
            mutedDash
        else
            report.CompositionSeam.SecurityHeaderKeys
            |> List.map (fun k -> sprintf "<code>%s</code>" (encode k))
            |> String.concat ", "

    sb.AppendLine(sprintf "<tr><th>Security headers</th><td>%s</td></tr>" headerKeysCell)
    |> ignore

    let consumersCell =
        if report.CompositionSeam.NotificationConsumers.IsEmpty then
            mutedDash
        else
            report.CompositionSeam.NotificationConsumers
            |> List.map (fun s -> sprintf "<code>%s</code>" (encode s))
            |> String.concat ", "

    sb.AppendLine(sprintf "<tr><th>Notification consumers</th><td>%s</td></tr>" consumersCell)
    |> ignore

    sb.AppendLine "</table>" |> ignore

    sb.AppendLine
        """<p class="muted">Hook values are not dereferenced; security-header values are redacted (CSP / Permissions-Policy may carry deployment-private data).</p>"""
    |> ignore

    // Health checks (Phase 9k)
    sb.AppendLine(sprintf "<h2>Health checks (%d)</h2>" report.HealthChecks.Length)
    |> ignore

    // Phase 9p — cross-link to the production-safe operator UI. Same
    // per-probe data, accessible to Owner/Admin without enabling
    // `EnableDevEndpoints` in production.
    sb.AppendLine
        """<p class="muted"><em>Production operators with Owner/Admin role can view the same data at <code>Health Monitor</code> in the sidebar (when <code>ClientConfig.HealthMonitor &ne; NoHealthMonitor</code>).</em></p>"""
    |> ignore

    if report.HealthChecks.IsEmpty then
        sb.AppendLine
            """<p class="muted">No <code>IHealthCheck</code> probes registered. <code>/ready</code> always returns 200.</p>"""
        |> ignore
    else
        sb.AppendLine
            """<p class="muted">Same per-probe outcomes that back <code>/ready</code> — invoked in parallel with each probe's declared timeout. <code>Degraded</code> does not flip <code>/ready</code> to 503; only <code>Unhealthy</code> does.</p>"""
        |> ignore

        sb.AppendLine "<table><tr><th>Name</th><th>Kind</th><th>Timeout</th><th>Status</th><th>Message</th></tr>"
        |> ignore

        let statusClass =
            function
            | "Healthy" -> "ok"
            | "Degraded" -> "muted"
            | "Unhealthy" -> "err"
            | _ -> "muted"

        for h in report.HealthChecks do
            sb.AppendLine(
                sprintf
                    """<tr><td><code>%s</code></td><td>%s</td><td>%dms</td><td><span class="%s">%s</span></td><td>%s</td></tr>"""
                    (encode h.Name)
                    (encode h.Kind)
                    h.TimeoutMs
                    (statusClass h.Status)
                    (encode h.Status)
                    (if String.IsNullOrEmpty h.Message then
                         mutedDash
                     else
                         encode h.Message)
            )
            |> ignore

        sb.AppendLine "</table>" |> ignore

    // Config preflight (Phase 9m) — snapshot captured at compose end,
    // not re-run on dev-page hit. Status reflects the most recent
    // startup; restart the deployment to refresh.
    sb.AppendLine(sprintf "<h2>Config preflight (%d)</h2>" report.Validators.Length)
    |> ignore

    // Phase 9p — cross-link to the production-safe operator UI.
    // Same snapshot, accessible to Owner/Admin without enabling
    // `EnableDevEndpoints` in production.
    sb.AppendLine
        """<p class="muted"><em>Production operators with Owner/Admin role can view the same snapshot at <code>Health Monitor &gt; Preflight</code> in the sidebar (when <code>ClientConfig.HealthMonitor &ne; NoHealthMonitor</code>).</em></p>"""
    |> ignore

    if report.Validators.IsEmpty then
        sb.AppendLine
            """<p class="muted">No <code>IConfigValidator</code> registered, or <code>SkipPreflight = true</code>. Startup proceeded without preflight.</p>"""
        |> ignore
    else
        sb.AppendLine
            """<p class="muted">Snapshot from the most recent startup. <code>Error</code> outcomes aborted the deploy; <code>Warning</code> logged at <code>Warn</code> and continued; <code>Ok</code> is the silent default.</p>"""
        |> ignore

        sb.AppendLine "<table><tr><th>Name</th><th>Status</th><th>Elapsed</th><th>Message</th></tr>"
        |> ignore

        let validatorClass =
            function
            | "Ok" -> "ok"
            | "Warning" -> "muted"
            | "Error" -> "err"
            | _ -> "muted"

        for v in report.Validators do
            sb.AppendLine(
                sprintf
                    """<tr><td><code>%s</code></td><td><span class="%s">%s</span></td><td>%dms</td><td>%s</td></tr>"""
                    (encode v.Name)
                    (validatorClass v.Status)
                    (encode v.Status)
                    v.ElapsedMs
                    (if String.IsNullOrEmpty v.Message then
                         mutedDash
                     else
                         encode v.Message)
            )
            |> ignore

        sb.AppendLine "</table>" |> ignore

    // Lightweight profile (Phase 1g)
    sb.AppendLine(sprintf "<h2>Lightweight profile (%d)</h2>" report.LightweightFeatures.Length)
    |> ignore

    if report.LightweightFeatures.IsEmpty then
        sb.AppendLine """<p class="muted">No gateable features registered.</p>"""
        |> ignore
    else
        sb.AppendLine
            """<p class="muted">Each row is a feature whose registration the SDK gates on a <code>ServerConfig</code> field. <code>Active</code> reflects the resolved mode after auto-detection (e.g., <code>NotificationsAuto</code>). The lightweight default keeps <code>Active</code> false on every row except <code>Mode</code> and the always-on infrastructure.</p>"""
        |> ignore

        sb.AppendLine "<table><tr><th>Feature</th><th>Mode</th><th>Active</th><th>Config path</th></tr>"
        |> ignore

        for f in report.LightweightFeatures do
            sb.AppendLine(
                sprintf
                    "<tr><td>%s</td><td><code>%s</code></td><td>%s</td><td><code>%s</code></td></tr>"
                    (encode f.Feature)
                    (encode f.Mode)
                    (mark f.Active)
                    (encode f.ConfigPath)
            )
            |> ignore

        sb.AppendLine "</table>" |> ignore

    // DI services
    sb.AppendLine(sprintf "<h2>DI services (%d)</h2>" report.Services.Length)
    |> ignore

    sb.AppendLine """<p class="muted">Type names only — no instances are dereferenced.</p>"""
    |> ignore

    sb.AppendLine "<table><tr><th>Service type</th><th>Lifetime</th><th>Implementation</th></tr>"
    |> ignore

    for s in report.Services do
        let impl =
            s.Implementation
            |> Option.map (fun i -> sprintf "<code>%s</code>" (encode i))
            |> Option.defaultValue mutedDash

        sb.AppendLine(
            sprintf
                "<tr><td><code>%s</code></td><td>%s</td><td>%s</td></tr>"
                (encode s.ServiceType)
                (encode s.Lifetime)
                impl
        )
        |> ignore

    sb.AppendLine "</table>" |> ignore

    // Phase 6h follow-up — Workstream B. Contributor panels.
    if not (Map.isEmpty report.Contributors) then
        sb.AppendLine "<hr><h2>Contributor panels</h2>" |> ignore

        sb.AppendLine
            """<p class="muted">Output from registered <code>IDevDiagnosticsContributor</code> instances. Each panel is one contributor's serialised payload.</p>"""
        |> ignore

        for (name, payloadJson) in report.Contributors |> Map.toList |> List.sortBy fst do
            sb.AppendLine(sprintf "<h3>%s</h3>" (encode name)) |> ignore
            sb.AppendLine(sprintf "<pre>%s</pre>" (encode payloadJson)) |> ignore

    sb.AppendLine "</body></html>" |> ignore
    sb.ToString()

// ─── Route handlers ──────────────────────────────────────────────────

/// JSON handler for `/dev/inspect`. Sets `Cache-Control: no-store` so
/// browser caches don't surprise developers with a stale view of mid-
/// edit DI state.
let private jsonHandler (config: ServerConfig) (capture: DevDiagnosticsCapture) : HttpHandler =
    fun next ctx -> task {
        let! report = buildReport config capture ctx
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        ctx.Response.Headers["Cache-Control"] <- "no-store"
        do! ctx.Response.WriteAsync(renderJson report)
        return! next ctx
    }

let private htmlHandler (config: ServerConfig) (capture: DevDiagnosticsCapture) : HttpHandler =
    fun next ctx -> task {
        let! report = buildReport config capture ctx
        ctx.Response.ContentType <- "text/html; charset=utf-8"
        ctx.Response.Headers["Cache-Control"] <- "no-store"
        do! ctx.Response.WriteAsync(renderHtml report)
        return! next ctx
    }

/// Routes for the dev diagnostics endpoint. `/dev/inspect/html` is the
/// browser-friendly view; `/dev/inspect` is JSON. Order matters here —
/// the more specific `/html` route matches first inside Giraffe's
/// `choose`, otherwise `/dev/inspect` would shadow it.
let routes (config: ServerConfig) (capture: DevDiagnosticsCapture) : HttpHandler list = [
    route "/dev/inspect/html" >=> htmlHandler config capture
    route "/dev/inspect" >=> jsonHandler config capture
]
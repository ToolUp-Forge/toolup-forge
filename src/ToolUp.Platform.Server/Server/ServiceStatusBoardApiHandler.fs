module ToolUp.Platform.ServiceStatusBoardApiHandler

open System
open System.Threading
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidatorAggregator
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.SmokeTests

// ─── Phase 9p.A — IServiceStatusBoardApi handler ─────────────────────
//
// Composition handler that fans out to every per-section builder in
// parallel with a per-section timeout, then aggregates the results
// into a single `ServiceStatusSnapshot`. Not a new substrate interface
// — every section reads from an already-audited surface (`IHealthCheck`,
// `IPreflightSnapshot`, `IAuditLog`, `IJobStore`, `IEventStore`) and
// the section-disable check is driven by the matching `ServerConfig`
// mode field (`JobScheduler` / `RateLimiter` / `ConfigDriftDetection`
// / `SmokeTest`).
//
// **Per-section timeout.** Each builder runs under a 3s cap (modest
// budget — the underlying reads are intentionally cheap; missing the
// cap is a deployment-quality signal). A builder that exceeds it
// surfaces as `SectionSummary.failure` with `Severity = Error` rather
// than blocking the snapshot.
//
// **Platform-Admin gate.** Mirrors `HealthMonitorApiHandler` — the
// `AccessContext` resolver is the same shape; the gate is
// `canModifyPlatformConfig`. Mode-agnostic: a bootstrapped admin in
// any mode (including `Anonymous` with a dev-bootstrapped admin)
// reaches the board because deployment-wide status is a load-bearing
// part of the admin's job.

let private perSectionBudget = TimeSpan.FromSeconds 3.0

/// Cap a list's length and report whether truncation happened. Used
/// to bound `SectionSummary.Details` so a section with many failing
/// probes / many drift paths doesn't bloat the wire payload.
let private capDetails (maxItems: int) (items: string list) : string list =
    if items.Length <= maxItems then
        items
    else
        let head = items |> List.truncate maxItems
        head @ [ sprintf "… (+%d more)" (items.Length - maxItems) ]

let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        let teamId =
            match ctx.Items.TryGetValue "ToolUp.StorageScope" with
            | true, (:? StorageScope as s) when s.Container.StartsWith "team-" -> Some s.ScopeId
            | _ -> None

        AccessContext.unrestricted (AnonymousSession userId)

let private ensureReadAllowed (accessContext: AccessContext) : Async<Result<unit, string>> = async {
    if AccessContext.canModifyPlatformConfig accessContext then
        return Ok()
    else
        return Error "platform admin role required"
}

/// Wrap a section-builder in the per-section timeout. A builder that
/// throws or exceeds the budget is reported as `SectionSummary.failure`
/// — the snapshot still ships, the overall status flips to `Error`.
let private withTimeout (sectionName: string) (build: Async<SectionSummary>) : Async<SectionSummary> = async {
    try
        use cts = new CancellationTokenSource(perSectionBudget)
        let work = Async.StartImmediateAsTask(build, cts.Token)

        try
            let! result = work |> Async.AwaitTask
            return result
        with :? OperationCanceledException ->
            return
                SectionSummary.failure
                    (sprintf "%s section unavailable." sectionName)
                    (sprintf "Per-section read exceeded %dms." (int perSectionBudget.TotalMilliseconds))
    with ex ->
        return
            SectionSummary.failure (sprintf "%s section unavailable." sectionName) (sprintf "Read threw: %s" ex.Message)
}

// ─── Per-section builders ────────────────────────────────────────────

let private buildHealth (ctx: HttpContext) : Async<HealthSummary> = async {
    let probes = ctx.RequestServices.GetServices<IHealthCheck>() |> List.ofSeq

    if probes.IsEmpty then
        return {
            Disabled = false
            DisabledReason = ""
            Severity = StatusSeverity.Ok
            Headline = "No health probes registered."
            Details = []
        }
    else
        let! runs = probes |> List.map HealthCheckRunner.runOne |> Async.Parallel
        let total = runs.Length

        let unhealthy =
            runs |> Array.filter (fun r -> r.Status = "Unhealthy") |> Array.toList

        let degraded = runs |> Array.filter (fun r -> r.Status = "Degraded") |> Array.toList

        let healthy = runs |> Array.filter (fun r -> r.Status = "Healthy") |> Array.toList

        let severity =
            if not unhealthy.IsEmpty then StatusSeverity.Error
            elif not degraded.IsEmpty then StatusSeverity.Warn
            else StatusSeverity.Ok

        let headline = sprintf "%d of %d probe(s) Healthy." healthy.Length total

        let details =
            (unhealthy |> List.map (fun r -> sprintf "Unhealthy: %s — %s" r.Name r.Message))
            @ (degraded |> List.map (fun r -> sprintf "Degraded: %s — %s" r.Name r.Message))
            |> capDetails 10

        return {
            Disabled = false
            DisabledReason = ""
            Severity = severity
            Headline = headline
            Details = details
        }
}

let private buildPreflight (ctx: HttpContext) : Async<PreflightSummary> = async {
    match ctx.RequestServices.GetService(typeof<IPreflightSnapshot>) with
    | :? IPreflightSnapshot as snap ->
        let outcomes = snap.LastRun

        if outcomes.IsEmpty then
            return {
                Disabled = false
                DisabledReason = ""
                Severity = StatusSeverity.Ok
                Headline = "No validators registered at last boot."
                Details = []
            }
        else
            let total = outcomes.Length

            let errors =
                outcomes
                |> List.filter (fun o -> ConfigValidation.ValidationResult.status o.Result = "Error")

            let warnings =
                outcomes
                |> List.filter (fun o -> ConfigValidation.ValidationResult.status o.Result = "Warning")

            let okCount = total - errors.Length - warnings.Length

            let severity =
                if not errors.IsEmpty then StatusSeverity.Error
                elif not warnings.IsEmpty then StatusSeverity.Warn
                else StatusSeverity.Ok

            let headline = sprintf "%d of %d validator(s) Ok at last boot." okCount total

            let details =
                (errors
                 |> List.map (fun o ->
                     sprintf "Error: %s — %s" o.Name (ConfigValidation.ValidationResult.message o.Result)))
                @ (warnings
                   |> List.map (fun o ->
                       sprintf "Warning: %s — %s" o.Name (ConfigValidation.ValidationResult.message o.Result)))
                |> capDetails 10

            return {
                Disabled = false
                DisabledReason = ""
                Severity = severity
                Headline = headline
                Details = details
            }
    | _ ->
        return
            SectionSummary.disabled
                "Preflight snapshot service not registered (deployment composed before Phase 9m, or SkipPreflight = true)."
}

let private buildDrift (ctx: HttpContext) (mode: ConfigDriftDetectionMode) : Async<DriftSummary> = async {
    match mode with
    | NoConfigDriftDetection ->
        return SectionSummary.disabled "Config drift detector disabled (ServerConfig.ConfigDriftDetection)."
    | EnabledConfigDriftDetection ->
        match ctx.RequestServices.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as auditLog ->
            let now = DateTime.UtcNow
            let lookback = now.AddHours -24.0

            let! drifts = auditLog.GetAuditTrail("_platform", Some(lookback, now), Some "ConfigDrift")

            if drifts.IsEmpty then
                return {
                    Disabled = false
                    DisabledReason = ""
                    Severity = StatusSeverity.Ok
                    Headline = "No drift detected in the last 24h."
                    Details = []
                }
            else
                return {
                    Disabled = false
                    DisabledReason = ""
                    Severity = StatusSeverity.Warn
                    Headline = sprintf "%d drift event(s) in the last 24h." drifts.Length
                    Details = [ "Drift recorded — inspect the Audit Log admin module for field-level paths." ]
                }
        | _ ->
            return
                SectionSummary.failure
                    "Drift detector enabled but audit log unavailable."
                    "ServerConfig.ConfigDriftDetection = EnabledConfigDriftDetection but IAuditLog could not be resolved from DI."
}

let private buildRateLimit (ctx: HttpContext) (mode: RateLimiterMode) : Async<RateLimitSummary> = async {
    // Phase 69g.tail — per-method `[<RateLimit>]` denial activity, read
    // from the audit trail. A denied call emits a `RateLimitExceeded`
    // `RemotingMethodAudited` row (dispatcher rate-limit pre-flight);
    // platform-API-record denials land at the `_platform` scope. Per-
    // tenant-scoped denials surface under their own scope — the same
    // cross-scope read-cost limitation noted for the outbound limiter
    // below — so this is the platform-scope denial signal, not a global
    // total. The read is cheap: one `_platform` audit query, mirroring
    // `buildDrift`.
    let! (rlSeverity, rlDetails) = async {
        match ctx.RequestServices.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as auditLog ->
            let now = DateTime.UtcNow
            let lookback = now.AddHours -24.0

            let! rows = auditLog.GetAuditTrail("_platform", Some(lookback, now), Some "RemotingMethodAudited")

            let denials =
                rows
                |> List.choose (fun evt ->
                    match evt with
                    | RemotingMethodAudited p when p.Kind = "RateLimitExceeded" -> Some p.MethodName
                    | _ -> None)

            if denials.IsEmpty then
                return StatusSeverity.Ok, []
            else
                let byMethod =
                    denials
                    |> List.countBy id
                    |> List.sortByDescending snd
                    |> List.map (fun (m, n) -> sprintf "  %s — %d denial(s) in the last 24h" m n)

                let headline =
                    sprintf
                        "Per-method [<RateLimit>] budgets denied %d call(s) at the platform scope in the last 24h:"
                        denials.Length

                return StatusSeverity.Warn, headline :: byMethod
        | _ -> return StatusSeverity.Ok, []
    }

    match mode with
    | NoRateLimiter ->
        // The outbound `IRateLimiter` is off, but per-method `[<RateLimit>]`
        // budgets are an independent dispatcher subsystem — still report
        // their denial activity if any.
        if rlDetails.IsEmpty then
            return
                SectionSummary.disabled
                    "Outbound rate limiter disabled (ServerConfig.RateLimiter). No per-method [<RateLimit>] denials at the platform scope in the last 24h."
        else
            return {
                Disabled = false
                DisabledReason = ""
                Severity = rlSeverity
                Headline = "Outbound rate limiter disabled; per-method [<RateLimit>] budgets active."
                Details = rlDetails
            }
    | EnabledRateLimiter ->
        // The `IRateLimiter` substrate does not expose a cross-scope
        // refused-count read — `RateLimitRefused` audit rows live under
        // per-tenant scope, not `_platform`, and walking every scope on
        // every snapshot request defeats the "cheap read" budget. Per-
        // scope refused-counts surface through the existing audit-log
        // admin module instead; this section reports substrate
        // enablement, the per-method denial signal, and points operators
        // to the deeper surface.
        return {
            Disabled = false
            DisabledReason = ""
            Severity = rlSeverity
            Headline = "Outbound rate limiter active."
            Details =
                "Per-scope refused-call detail: inspect RateLimitRefused / RateLimitWaited audit rows under the affected tenant scope."
                :: rlDetails
        }
}

let private buildJobQueue (ctx: HttpContext) (mode: JobSchedulerMode) : Async<JobQueueSummary> = async {
    match mode with
    | NoJobScheduler -> return SectionSummary.disabled "Job scheduler disabled (ServerConfig.JobScheduler)."
    | InProcessJobScheduler ->
        match ctx.RequestServices.GetService(typeof<IJobStore>) with
        | :? IJobStore as store ->
            let! scopes = store.ListScopesWithJobs()

            if scopes.IsEmpty then
                return {
                    Disabled = false
                    DisabledReason = ""
                    Severity = StatusSeverity.Ok
                    Headline = "Job scheduler active. 0 jobs scheduled."
                    Details = []
                }
            else
                let! perScope = scopes |> List.map (fun s -> store.ListJobs s) |> Async.Parallel
                let allJobs = perScope |> Array.collect List.toArray

                let active = allJobs |> Array.filter (fun j -> j.Status = JobStatus.Active)
                let disabled = allJobs |> Array.filter (fun j -> j.Status = JobStatus.Disabled)
                let cancelled = allJobs |> Array.filter (fun j -> j.Status = JobStatus.Cancelled)

                let failing =
                    active |> Array.filter (fun j -> j.ConsecutiveFailures > 0) |> Array.toList

                let severity =
                    if not failing.IsEmpty then
                        StatusSeverity.Warn
                    else
                        StatusSeverity.Ok

                let headline =
                    sprintf
                        "%d active, %d disabled, %d cancelled across %d scope(s)."
                        active.Length
                        disabled.Length
                        cancelled.Length
                        scopes.Length

                let details =
                    failing
                    |> List.map (fun j ->
                        sprintf
                            "Failing: handler %s (%d consecutive failure(s) under scope %s)"
                            j.Handler
                            j.ConsecutiveFailures
                            j.ScopeId)
                    |> capDetails 10

                return {
                    Disabled = false
                    DisabledReason = ""
                    Severity = severity
                    Headline = headline
                    Details = details
                }
        | _ ->
            return
                SectionSummary.failure
                    "Job scheduler enabled but job store unavailable."
                    "ServerConfig.JobScheduler = InProcessJobScheduler but IJobStore could not be resolved from DI."
}

let private buildSmokeTest (ctx: HttpContext) (mode: SmokeTestMode) : Async<SmokeTestSummary> = async {
    match mode with
    | NoSmokeTest -> return SectionSummary.disabled "Smoke-test endpoint disabled (ServerConfig.SmokeTest)."
    | EnabledSmokeTest ->
        match ctx.RequestServices.GetService(typeof<IEventStore>) with
        | :? IEventStore as store ->
            let! events = store.ReadBySource("_platform", SmokeTest.AuditSourceModule)

            let smokeRuns =
                events
                |> List.filter (fun e -> e.EventType = SmokeTest.AuditEventType)
                |> List.sortByDescending _.OccurredAt

            match smokeRuns with
            | [] ->
                return {
                    Disabled = false
                    DisabledReason = ""
                    Severity = StatusSeverity.Ok
                    Headline = "Smoke endpoint mounted; no runs recorded yet."
                    Details = [
                        "Smoke tests do not auto-run — trigger via the deploy pipeline's POST to /api/_internal/smoke."
                    ]
                }
            | latest :: _ ->
                // Payload status is captured by `SmokeTestHandler.SmokeResponse.status`
                // ("Pass" / "Fail"). The payload schema is internal to the
                // handler so we do not re-parse it here; we surface the
                // wall-clock of the latest run and a pointer to the audit
                // log for operators that need the per-test detail.
                let ageHours = (DateTime.UtcNow - latest.OccurredAt).TotalHours

                let severity =
                    if ageHours > 24.0 then
                        StatusSeverity.Warn
                    else
                        StatusSeverity.Ok

                let stalenessNote =
                    if ageHours > 24.0 then
                        sprintf "Latest smoke run is %.1f hours old (>24h)." ageHours
                    else
                        sprintf "Latest smoke run %.1f hours ago." ageHours

                return {
                    Disabled = false
                    DisabledReason = ""
                    Severity = severity
                    Headline = stalenessNote
                    Details = [
                        "Per-test status / message: inspect the SmokeTestRun audit row under _platform.diagnostics."
                    ]
                }
        | _ ->
            return
                SectionSummary.failure
                    "Smoke endpoint enabled but event store unavailable."
                    "ServerConfig.SmokeTest = EnabledSmokeTest but IEventStore could not be resolved from DI."
}

// ─── Composite snapshot builder ──────────────────────────────────────

let private buildSnapshot (config: ServerConfig) (ctx: HttpContext) : Async<ServiceStatusSnapshot> = async {
    let health = withTimeout ServiceStatusSnapshot.HealthSection (buildHealth ctx)

    let preflight =
        withTimeout ServiceStatusSnapshot.PreflightSection (buildPreflight ctx)

    let drift =
        withTimeout ServiceStatusSnapshot.DriftSection (buildDrift ctx config.ConfigDriftDetection)

    let rateLimit =
        withTimeout ServiceStatusSnapshot.RateLimitSection (buildRateLimit ctx config.RateLimiter)

    let jobQueue =
        withTimeout ServiceStatusSnapshot.JobQueueSection (buildJobQueue ctx config.JobScheduler)

    let smokeTest =
        withTimeout ServiceStatusSnapshot.SmokeTestSection (buildSmokeTest ctx config.SmokeTest)

    // Fan out in parallel — `Async.Parallel` over the six sections.
    // Mixed return types via the per-section type abbreviations; all
    // alias `SectionSummary` so a heterogeneous list compiles.
    let! results = Async.Parallel [ health; preflight; drift; rateLimit; jobQueue; smokeTest ]

    let h = results[0]
    let p = results[1]
    let d = results[2]
    let r = results[3]
    let j = results[4]
    let s = results[5]

    let overall = ServiceStatusSnapshot.computeOverall h p d r j s

    return {
        GeneratedAt = DateTime.UtcNow
        Overall = overall
        Health = h
        Preflight = p
        Drift = d
        RateLimit = r
        JobQueue = j
        SmokeTest = s
    }
}

/// Build the `IServiceStatusBoardApi` Fable.Remoting handler.
/// `ServerConfig` is closed over at compose time so the handler reads
/// the section-disable mode fields without re-resolving a config
/// singleton per request.
let serviceStatusBoardApi (config: ServerConfig) (ctx: HttpContext) : IServiceStatusBoardApi =
    let accessContext = resolveAccessContext ctx

    let withGate (work: unit -> Async<Result<'T, string>>) = async {
        let! gate = ensureReadAllowed accessContext

        match gate with
        | Error msg -> return Error msg
        | Ok() -> return! work ()
    }

    {
        GetSnapshot =
            fun () ->
                withGate (fun () -> async {
                    let! snap = buildSnapshot config ctx
                    return Ok snap
                })

        RefreshHealth =
            fun () ->
                withGate (fun () -> async {
                    let! summary = withTimeout ServiceStatusSnapshot.HealthSection (buildHealth ctx)
                    return Ok summary
                })

        RefreshPreflight =
            fun () ->
                withGate (fun () -> async {
                    let! summary = withTimeout ServiceStatusSnapshot.PreflightSection (buildPreflight ctx)
                    return Ok summary
                })

        RefreshDrift =
            fun () ->
                withGate (fun () -> async {
                    let! summary =
                        withTimeout ServiceStatusSnapshot.DriftSection (buildDrift ctx config.ConfigDriftDetection)

                    return Ok summary
                })

        RefreshRateLimit =
            fun () ->
                withGate (fun () -> async {
                    let! summary =
                        withTimeout ServiceStatusSnapshot.RateLimitSection (buildRateLimit ctx config.RateLimiter)

                    return Ok summary
                })

        RefreshJobQueue =
            fun () ->
                withGate (fun () -> async {
                    let! summary =
                        withTimeout ServiceStatusSnapshot.JobQueueSection (buildJobQueue ctx config.JobScheduler)

                    return Ok summary
                })

        RefreshSmokeTest =
            fun () ->
                withGate (fun () -> async {
                    let! summary =
                        withTimeout ServiceStatusSnapshot.SmokeTestSection (buildSmokeTest ctx config.SmokeTest)

                    return Ok summary
                })
    }
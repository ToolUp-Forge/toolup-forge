module ToolUp.Platform.HealthMonitorApiHandler

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.ConfigValidatorAggregator

// ─── Phase 9p — HealthMonitorApi handler ─────────────────────────────
//
// Production-safe surface mirroring the dev endpoint's health +
// preflight panels.
//
// **Phase 4b re-gate (2026-05-10).** Originally Owner/Admin (per-team)
// gated; the GP 4 carve-out documented at Phase 9p ship time
// acknowledged that probes were deployment-wide data exposed under a
// team-level role gate. With Phase 4b's `PlatformRole.PlatformAdmin`
// existing, the gate has a structurally cleaner home: deployment-wide
// data is gated by the deployment-wide role.
//
// **Phase 4b follow-up (2026-05-10, post-smoke-test).** The original
// re-gate kept an Anonymous-mode short-circuit ("Health monitor is not
// available in this mode") as Phase 9p belt-and-suspenders. That was
// outdated by Phase 4b: `IPlatformAdminStore.IsPlatformAdmin` resolves
// the role for any non-anonymous caller regardless of the deployment shape,
// so a bootstrapped admin in any mode (including `Anonymous` with a
// dev-bootstrapped admin) legitimately holds the role and should reach
// the health panels — viewing health is a load-bearing part of the
// admin's job. Removed the mode-based pre-filter; the role gate is
// the only gate. Anonymous callers without the admin role still get
// `"platform admin role required"`, which is the same outcome the
// short-circuit produced for them — the change is for the bootstrapped-
// admin-in-Anonymous-mode case (rare in production, common in dev).

/// Auth-context resolver mirroring `WebhookApiHandler.accessContext` /
/// `ConfigHandler.accessContext`. Falls back to a synthetic
/// `Anonymous` context for tests that bypass `ScopeResolutionMiddleware`
/// — production paths always populate the scoped DI service via the
/// middleware.
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

/// Platform-admin read gate. Returns `Ok ()` only when the caller
/// holds `PlatformRole.PlatformAdmin`; everyone else (including Team
/// Owners / Admins who do NOT also hold the platform-admin role,
/// AND anonymous callers in `Anonymous` mode) receives
/// `Error "platform admin role required"`. Mode-agnostic — a
/// bootstrapped admin in any mode reaches the panels because the
/// role gate accepts them regardless of the deployment shape.
let private ensureReadAllowed (accessContext: AccessContext) : Async<Result<unit, string>> = async {
    if AccessContext.canModifyPlatformConfig accessContext then
        return Ok()
    else
        return Error "platform admin role required"
}

/// Build the `IHealthMonitorApi` Fable.Remoting handler. Resolves
/// `IHealthCheck`s, `IPreflightSnapshot`, and `AccessContext` lazily
/// from DI per request — same idiom as `WebhookApiHandler.webhookApi`
/// and `ConfigHandler.configApi`.
let healthMonitorApi (ctx: HttpContext) : IHealthMonitorApi =
    let accessContext = resolveAccessContext ctx

    let withGate (work: unit -> Async<Result<'T, string>>) = async {
        let! gate = ensureReadAllowed accessContext

        match gate with
        | Error msg -> return Error msg
        | Ok() -> return! work ()
    }

    {
        GetCurrentHealth =
            fun () ->
                withGate (fun () -> async {
                    let probes = ctx.RequestServices.GetServices<IHealthCheck>() |> List.ofSeq

                    if probes.IsEmpty then
                        return
                            Ok {
                                GeneratedAt = DateTime.UtcNow
                                Probes = []
                            }
                    else
                        let! runs = probes |> List.map HealthCheckRunner.runOne |> Async.Parallel

                        let probeViews =
                            runs
                            |> Array.map (fun r -> {
                                Name = r.Name
                                Kind = HealthKind.toString r.Kind
                                TimeoutMs = r.TimeoutMs
                                Status = r.Status
                                Message = r.Message
                                ElapsedMs = r.ElapsedMs
                            })
                            |> Array.toList

                        return
                            Ok {
                                GeneratedAt = DateTime.UtcNow
                                Probes = probeViews
                            }
                })

        GetPreflightSnapshot =
            fun () ->
                withGate (fun () -> async {
                    match ctx.RequestServices.GetService(typeof<IPreflightSnapshot>) with
                    | :? IPreflightSnapshot as snap ->
                        let outcomes =
                            snap.LastRun
                            |> List.map (fun o -> {
                                Name = o.Name
                                Status = ConfigValidation.ValidationResult.status o.Result
                                Message = ConfigValidation.ValidationResult.message o.Result
                                ElapsedMs = o.ElapsedMs
                            })

                        return
                            Ok {
                                HasSnapshot = true
                                Outcomes = outcomes
                            }
                    | _ -> return Ok { HasSnapshot = false; Outcomes = [] }
                })

        GetJobSchedulerTelemetry =
            fun () ->
                withGate (fun () -> async {
                    match ctx.RequestServices.GetService(typeof<IJobSchedulerTelemetry>) with
                    | :? IJobSchedulerTelemetry as telemetry ->
                        let snap = telemetry.Snapshot()

                        return
                            Ok {
                                HasScheduler = true
                                TickMissedCount60Min = snap.TickMissedCount60Min
                                LastDriftMs = snap.LastDriftMs
                                LastTickMissedAt = snap.LastTickMissedAt
                                GeneratedAt = snap.GeneratedAt
                            }
                    | _ ->
                        return
                            Ok {
                                HasScheduler = false
                                TickMissedCount60Min = 0
                                LastDriftMs = None
                                LastTickMissedAt = None
                                GeneratedAt = DateTime.UtcNow
                            }
                })
    }
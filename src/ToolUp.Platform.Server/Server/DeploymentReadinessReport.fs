// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.DeploymentReadinessReport

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidatorAggregator
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.SmokeTests

// Deliberately NOT opening `ToolUp.Platform.ConfigValidation` — its
// `ValidationResult` cases (`Ok | Warning | Error`) would shadow the
// standard `Result<_,_>` constructors used in the handler gate.
// `ValidationResult.status` is referenced through the qualified
// `ConfigValidation.ValidationResult.status` in the one place needed,
// mirroring `ServiceStatusBoardApiHandler`.

// ─── Phase 177 — DeploymentReadinessReport gatherer + handler ────────
//
// Reads the four already-shipped operability signals and folds them
// into one `DeploymentReadinessReport` via the pure
// `DeploymentReadiness.summarise`. Not a new substrate interface — every
// source is read from an already-audited surface (`IPreflightSnapshot`,
// `ISmokeTest`, `IAuditLog`'s `ConfigDrift` rows, `IHealthCheck`), and
// each source is independently `NotComposed` when its substrate isn't
// wired, so a deployment that composes a subset (or none) of the signals
// gets an honest partial scorecard rather than a fabricated pass (GP 13).
//
// **Live smoke + health.** Unlike the passive `ServiceStatusBoard`
// (polled often, so it reads the latest smoke audit row), the readiness
// scorecard is an explicit, rarely-invoked operator go/no-go: it RUNS
// the registered `ISmokeTest` probes and `IHealthCheck` probes live so
// the verdict reflects "right now" and can name the failing item(s).
// Smoke probes run against the reserved `_smoke` sentinel scope and
// clean up after themselves (the `ISmokeTest` contract), so a readiness
// read cannot corrupt tenant data.
//
// **Platform-Admin gate.** Mirrors `HealthMonitorApiHandler` /
// `ServiceStatusBoardApiHandler` — the `AccessContext` resolver is the
// same shape; the gate is `canModifyPlatformConfig`. Mode-agnostic: a
// bootstrapped admin in any mode reaches the read. Anonymous / non-admin
// callers receive `Error "platform admin role required"`. The read is
// deployment-wide and carries no per-tenant data (GP 4).

let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        AccessContext.unrestricted (AnonymousSession userId)

let private ensureReadAllowed (accessContext: AccessContext) : Async<Result<unit, string>> = async {
    if AccessContext.canModifyPlatformConfig accessContext then
        return Ok()
    else
        return Error "platform admin role required"
}

// ─── Per-source gatherers ────────────────────────────────────────────

/// Read `IPreflightSnapshot.LastRun`. Service absent ⇒ `NotComposed`
/// (deployment composed before Phase 9m, or `SkipPreflight = true`).
/// Present-but-empty ⇒ `Clean` (preflight subsystem wired, zero issues).
let private gatherPreflight (ctx: HttpContext) : DeploymentReadiness.PreflightSummary =
    match ctx.RequestServices.GetService(typeof<IPreflightSnapshot>) with
    | :? IPreflightSnapshot as snap ->
        let outcomes = snap.LastRun

        let errors =
            outcomes
            |> List.filter (fun o -> ConfigValidation.ValidationResult.status o.Result = "Error")
            |> List.map _.Name

        let warnings =
            outcomes
            |> List.filter (fun o -> ConfigValidation.ValidationResult.status o.Result = "Warning")
            |> List.map _.Name

        let status =
            if not errors.IsEmpty then SourceStatus.Failed
            elif not warnings.IsEmpty then SourceStatus.Warned
            else SourceStatus.Clean

        {
            Status = status
            Total = outcomes.Length
            Errors = errors
            Warnings = warnings
        }
    | _ -> DeploymentReadiness.PreflightSummary.notComposed

/// Run every registered `ISmokeTest` once, in parallel. Disabled mode ⇒
/// `NotComposed`. Enabled but no probes registered ⇒ `Clean` (subsystem
/// on, nothing to fail). Any `Fail` ⇒ `Failed`, naming the probes.
let private gatherSmoke (config: ServerConfig) (ctx: HttpContext) : Async<DeploymentReadiness.SmokeSummary> = async {
    match config.SmokeTest with
    | NoSmokeTest -> return DeploymentReadiness.SmokeSummary.notComposed
    | EnabledSmokeTest ->
        let tests = ctx.RequestServices.GetServices<ISmokeTest>() |> List.ofSeq

        if tests.IsEmpty then
            return {
                Status = SourceStatus.Clean
                Total = 0
                Failures = []
            }
        else
            let! results =
                tests
                |> List.map (fun t -> async {
                    let! r = t.RunOnce()
                    return t.Name, r
                })
                |> Async.Parallel

            let failures =
                results
                |> Array.choose (fun (name, r) ->
                    match r with
                    | Fail _ -> Some name
                    | Pass -> None)
                |> Array.toList

            let status =
                if failures.IsEmpty then
                    SourceStatus.Clean
                else
                    SourceStatus.Failed

            return {
                Status = status
                Total = results.Length
                Failures = failures
            }
}

/// Read the `ConfigDrift` audit rows in the last 24h. Disabled mode ⇒
/// `NotComposed`. Drift detected ⇒ `Warned` (drift is never a hard
/// go/no-go failure — a drifted deployment can still serve). Enabled but
/// the audit log can't be resolved ⇒ `Warned` (degraded confidence — the
/// signal can't be read, but that is not a fabricated pass).
let private gatherDrift (config: ServerConfig) (ctx: HttpContext) : Async<DeploymentReadiness.DriftSummary> = async {
    match config.ConfigDriftDetection with
    | NoConfigDriftDetection -> return DeploymentReadiness.DriftSummary.notComposed
    | EnabledConfigDriftDetection ->
        match ctx.RequestServices.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as auditLog ->
            let now = DateTime.UtcNow
            let lookback = now.AddHours -24.0

            let! drifts = auditLog.GetAuditTrail("_platform", Some(lookback, now), Some "ConfigDrift")

            let status =
                if drifts.IsEmpty then
                    SourceStatus.Clean
                else
                    SourceStatus.Warned

            return {
                Status = status
                DriftEventCount = drifts.Length
            }
        | _ ->
            return {
                Status = SourceStatus.Warned
                DriftEventCount = 0
            }
}

/// Run every registered `IHealthCheck` once, in parallel. No probes
/// registered ⇒ `NotComposed` (no health signal wired). Any `Unhealthy`
/// ⇒ `Failed`; else any `Degraded` ⇒ `Warned`; else `Clean`. Names the
/// offending probes.
let private gatherHealth (ctx: HttpContext) : Async<DeploymentReadiness.HealthSummary> = async {
    let probes = ctx.RequestServices.GetServices<IHealthCheck>() |> List.ofSeq

    if probes.IsEmpty then
        return DeploymentReadiness.HealthSummary.notComposed
    else
        let! runs = probes |> List.map HealthCheckRunner.runOne |> Async.Parallel

        let unhealthy =
            runs
            |> Array.filter (fun r -> r.Status = "Unhealthy")
            |> Array.map _.Name
            |> Array.toList

        let degraded =
            runs
            |> Array.filter (fun r -> r.Status = "Degraded")
            |> Array.map _.Name
            |> Array.toList

        let status =
            if not unhealthy.IsEmpty then SourceStatus.Failed
            elif not degraded.IsEmpty then SourceStatus.Warned
            else SourceStatus.Clean

        return {
            Status = status
            Total = runs.Length
            Unhealthy = unhealthy
            Degraded = degraded
        }
}

/// Gather the four signals and fold them into a verdict via the pure
/// `DeploymentReadiness.summarise`.
let buildReport (config: ServerConfig) (ctx: HttpContext) : Async<DeploymentReadiness.DeploymentReadinessReport> = async {
    let preflight = gatherPreflight ctx
    let! smoke = gatherSmoke config ctx
    let! drift = gatherDrift config ctx
    let! health = gatherHealth ctx
    return DeploymentReadiness.summarise DateTime.UtcNow preflight smoke drift health
}

/// Build the `IDeploymentReadinessApi` Fable.Remoting handler.
/// `ServerConfig` is closed over at compose time so the handler reads the
/// per-source mode fields without re-resolving a config singleton per
/// request — same idiom as `ServiceStatusBoardApiHandler`.
let deploymentReadinessApi (config: ServerConfig) (ctx: HttpContext) : IDeploymentReadinessApi =
    let accessContext = resolveAccessContext ctx

    let withGate (work: unit -> Async<Result<'T, string>>) = async {
        let! gate = ensureReadAllowed accessContext

        match gate with
        | Error msg -> return Error msg
        | Ok() -> return! work ()
    }

    {
        GetReadinessReport =
            fun () ->
                withGate (fun () -> async {
                    let! report = buildReport config ctx
                    return Ok report
                })
    }
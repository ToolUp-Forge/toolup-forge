module ToolUp.Platform.ServiceStatusBoardDepsValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 9p.A — service-status-board-deps validator ────────────────
//
// Preflight warning when every observability surface the
// ServiceStatusBoard composes is disabled. The board still auto-mounts
// (the client default is `DefaultServiceStatusBoard`) but every section
// renders the disabled banner — operators see a uniformly empty page.
// Warning, not Error: the board has the Health + Preflight sections
// driven by `IHealthCheck` + `IPreflightSnapshot` regardless of these
// four mode fields, so "every section is disabled" is approximate.
// Deployments that legitimately want a barely-populated board (every
// observability surface deliberately off) keep booting; the Warn line
// is the signal that the sidebar entry is probably waste.

/// Warn when every `ServerConfig` mode field the board composes is
/// the `No*` case. Health + Preflight remain best-effort substrate-
/// driven sections (probes + validators may still be registered) so
/// the warning is advisory rather than blocking.
type ServiceStatusBoardDepsValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "service-status-board-deps"
        member _.Timeout = timeout

        member _.Validate() = async {
            let jobsOff = config.JobScheduler = NoJobScheduler
            let rateOff = config.RateLimiter = NoRateLimiter
            let driftOff = config.ConfigDriftDetection = NoConfigDriftDetection
            let smokeOff = config.SmokeTest = NoSmokeTest

            if jobsOff && rateOff && driftOff && smokeOff then
                return
                    Warning(
                        "ServiceStatusBoard composes JobQueue, RateLimit, Drift and SmokeTest sections, but ServerConfig disables all four (JobScheduler = NoJobScheduler, RateLimiter = NoRateLimiter, ConfigDriftDetection = NoConfigDriftDetection, SmokeTest = NoSmokeTest). The board will render disabled banners for those four sections; Health and Preflight remain best-effort. Set ClientConfig.ServiceStatusBoard = NoServiceStatusBoard to hide the sidebar entry, or enable at least one underlying substrate."
                    )
            else
                return Ok
        }
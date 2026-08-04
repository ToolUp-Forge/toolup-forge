module ToolUp.Platform.Tests.InProcess.ServiceStatusBoardApiHandlerTests

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.HealthChecks

// Deliberately NOT opening `ToolUp.Platform.ConfigValidation` here —
// its `ValidationResult` cases (`Ok | Warning | Error`) would shadow
// the standard `Result<_,_>` constructors used throughout these tests.
// `IConfigValidator` is referenced through `ConfigValidation.IConfigValidator`
// in the one place that needs it.

// ─── Phase 9p.A — ServiceStatusBoardApi handler tests ────────────────
//
// Exercises the composition handler's two load-bearing behaviours:
//   1. Per-section auto-skip when the matching ServerConfig mode is
//      `No*` (the section reports `Disabled = true` and contributes
//      nothing to `OverallStatus`).
//   2. Platform-Admin gate: callers without the role receive
//      `Error "platform admin role required"`.
//
// Tests resolve the handler through DI exactly as the production
// route mount does — the handler closes over `ServerConfig` and
// reads substrate via `HttpContext.RequestServices`.

// ─── Helpers ─────────────────────────────────────────────────────────

/// Build an HttpContext with a custom `AccessContext` placed in
/// `ctx.RequestServices` so the handler's `resolveAccessContext`
/// returns the test-supplied role.
let private ctxWith (accessContext: AccessContext) (configure: IServiceCollection -> unit) : HttpContext =
    let services = ServiceCollection()
    configure services
    services.AddSingleton<AccessContext>(accessContext) |> ignore
    let sp = services.BuildServiceProvider() :> IServiceProvider

    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    ctx

let private platformAdminContext (configure: IServiceCollection -> unit) =
    let ac = {
        AccessContext.unrestricted (Subject.AnonymousSession "admin-user") with
            PlatformRole = Some PlatformRole.PlatformAdmin
    }

    ctxWith ac configure

let private nonAdminContext (configure: IServiceCollection -> unit) =
    let ac = AccessContext.unrestricted (Subject.AnonymousSession "regular-user")
    ctxWith ac configure

let private allDisabledConfig = {
    ServerConfig.defaults with
        JobScheduler = NoJobScheduler
        RateLimiter = NoRateLimiter
        ConfigDriftDetection = NoConfigDriftDetection
        SmokeTest = NoSmokeTest
}

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 9p.A — ServiceStatusBoardApi handler" [

        testCaseAsync "Non-admin caller receives 'platform admin role required'"
        <| async {
            let ctx = nonAdminContext ignore
            let api = ServiceStatusBoardApiHandler.serviceStatusBoardApi allDisabledConfig ctx

            let! result = api.GetSnapshot()

            match result with
            | Error msg -> Expect.equal msg "platform admin role required" "non-admin caller gate message"
            | Ok _ -> failtest "expected Error from non-admin caller"
        }

        testCaseAsync "All-disabled config produces AllOk with every section Disabled"
        <| async {
            let ctx = platformAdminContext ignore
            let api = ServiceStatusBoardApiHandler.serviceStatusBoardApi allDisabledConfig ctx

            let! result = api.GetSnapshot()

            match result with
            | Ok snap ->
                Expect.isTrue snap.JobQueue.Disabled "JobQueue disabled when JobScheduler = No"
                Expect.isTrue snap.RateLimit.Disabled "RateLimit disabled when RateLimiter = No"
                Expect.isTrue snap.Drift.Disabled "Drift disabled when ConfigDriftDetection = No"
                Expect.isTrue snap.SmokeTest.Disabled "SmokeTest disabled when SmokeTest = No"
                // Health is best-effort substrate-driven; with no IHealthCheck
                // services registered it still reports `Disabled = false`
                // with an "no probes registered" Ok shape (best-effort
                // semantic).
                Expect.isFalse snap.Health.Disabled "Health section is best-effort"
                // Preflight reads `IPreflightSnapshot` from DI; with no
                // service registered it reports `Disabled = true` with a
                // "snapshot service not registered" reason. The composite
                // still aggregates to AllOk because disabled sections
                // contribute nothing.
                Expect.isTrue snap.Preflight.Disabled "Preflight disabled when no IPreflightSnapshot in DI"

                match snap.Overall with
                | AllOk -> ()
                | other -> failtestf "expected AllOk with all disabled, got %A" other
            | Error e -> failtestf "expected Ok, got Error %s" e
        }

        testCaseAsync "JobQueue section reports active job counts when scheduler enabled"
        <| async {
            let jobs = [
                {
                    JobId = Guid.NewGuid()
                    ScopeId = "team-alpha"
                    Handler = "noop-handler"
                    Payload = "{}"
                    Trigger = Trigger.Manual
                    Idempotency = None
                    RetryPolicy = JobRetryPolicy.defaults
                    ShardKey = None
                    Precision = JobPrecision.Minute
                    Status = JobStatus.Active
                    CreatedAt = DateTime.UtcNow
                    CreatedBy = "test"
                    NextRunAt = None
                    LastRunAt = None
                    LastRunStatus = None
                    LastRunError = None
                    ConsecutiveFailures = 0
                    Tags = Map.empty
                }
                {
                    JobId = Guid.NewGuid()
                    ScopeId = "team-alpha"
                    Handler = "flaky-handler"
                    Payload = "{}"
                    Trigger = Trigger.Manual
                    Idempotency = None
                    RetryPolicy = JobRetryPolicy.defaults
                    ShardKey = None
                    Precision = JobPrecision.Minute
                    Status = JobStatus.Active
                    CreatedAt = DateTime.UtcNow
                    CreatedBy = "test"
                    NextRunAt = None
                    LastRunAt = None
                    LastRunStatus = None
                    LastRunError = None
                    ConsecutiveFailures = 3
                    Tags = Map.empty
                }
            ]

            let store =
                { new IJobStore with
                    member _.Save(_) = async { return () }
                    member _.Get(_, _) = async { return None }
                    member _.ListJobs(scope) = async { return jobs |> List.filter (fun j -> j.ScopeId = scope) }
                    member _.Update(_) = async { return () }
                    member _.FindByIdempotencyKey(_, _, _, _) = async { return None }
                    member _.RecordRun(_) = async { return () }
                    member _.GetRecentRuns(_, _, _) = async { return [] }
                    member _.DueJobs(_, _) = async { return [] }
                    // Phase 319 — this stub exercises the job-queue
                    // status board, which reads definitions only; no run
                    // is ever awaiting external compute here.
                    member _.AwaitingExternalRuns(_, _) = async { return [] }
                    member _.ListScopesWithJobs() = async { return [ "team-alpha" ] }
                }

            let config = {
                allDisabledConfig with
                    JobScheduler = InProcessJobScheduler
            }

            let ctx = platformAdminContext (fun s -> s.AddSingleton<IJobStore>(store) |> ignore)

            let api = ServiceStatusBoardApiHandler.serviceStatusBoardApi config ctx

            let! result = api.RefreshJobQueue()

            match result with
            | Ok summary ->
                Expect.isFalse summary.Disabled "JobQueue not disabled when scheduler enabled"
                Expect.equal summary.Severity StatusSeverity.Warn "non-zero ConsecutiveFailures → Warn"

                Expect.stringContains summary.Headline "2 active" "headline reports active count"

                Expect.isTrue
                    (summary.Details |> List.exists (fun d -> d.Contains "flaky-handler"))
                    "failing job named in details"
            | Error e -> failtestf "expected Ok, got Error %s" e
        }

        testCaseAsync "RateLimit section reports enabled when RateLimiter = EnabledRateLimiter"
        <| async {
            let config = {
                allDisabledConfig with
                    RateLimiter = EnabledRateLimiter
            }

            let ctx = platformAdminContext ignore
            let api = ServiceStatusBoardApiHandler.serviceStatusBoardApi config ctx

            let! result = api.RefreshRateLimit()

            match result with
            | Ok summary ->
                Expect.isFalse summary.Disabled "RateLimit enabled"
                Expect.equal summary.Severity StatusSeverity.Ok "enabled-with-no-data is Ok"
                Expect.stringContains summary.Headline "active" "headline mentions active"
            | Error e -> failtestf "expected Ok, got Error %s" e
        }

        testCaseAsync "SmokeTest section reports 'no runs recorded' when EventStore has none"
        <| async {
            let store =
                { new IEventStore with
                    member _.Write(_) = async { return () }
                    member _.ReadAll(_) = async { return [] }
                    member _.ReadByType(_, _) = async { return [] }
                    member _.ReadBySource(_, _) = async { return [] }
                    member _.ListScopes() = async { return [] }

                    member _.Erase(_, _, _, _) = async {
                        return
                            Ok {
                                HandlerName = "test"
                                RecordsAffected = 0
                                Note = None
                            }
                    }
                }

            let config = {
                allDisabledConfig with
                    SmokeTest = EnabledSmokeTest
            }

            let ctx =
                platformAdminContext (fun s -> s.AddSingleton<IEventStore>(store) |> ignore)

            let api = ServiceStatusBoardApiHandler.serviceStatusBoardApi config ctx

            let! result = api.RefreshSmokeTest()

            match result with
            | Ok summary ->
                Expect.isFalse summary.Disabled "Smoke not disabled when EnabledSmokeTest"
                Expect.equal summary.Severity StatusSeverity.Ok "no-runs-yet is Ok"
                Expect.stringContains summary.Headline "no runs" "headline mentions no runs"
            | Error e -> failtestf "expected Ok, got Error %s" e
        }

        test "computeOverall aggregates per-section severity correctly" {
            // Pure helper — no DI / context needed; exercises the
            // client-shared aggregation logic that drives the snapshot's
            // top-line pill.
            let okSection: SectionSummary = {
                Disabled = false
                DisabledReason = ""
                Severity = StatusSeverity.Ok
                Headline = "fine"
                Details = []
            }

            let warnSection = {
                okSection with
                    Severity = StatusSeverity.Warn
            }

            let errSection = {
                okSection with
                    Severity = StatusSeverity.Error
            }

            let disabledSection = SectionSummary.disabled "off"

            let allOk =
                ServiceStatusSnapshot.computeOverall okSection okSection okSection okSection okSection okSection

            Expect.equal allOk AllOk "every-section-Ok rolls up to AllOk"

            let mixedWarn =
                ServiceStatusSnapshot.computeOverall okSection warnSection disabledSection okSection okSection okSection

            match mixedWarn with
            | DegradedBy ss -> Expect.contains ss ServiceStatusSnapshot.PreflightSection "Preflight named in DegradedBy"
            | other -> failtestf "expected DegradedBy, got %A" other

            let mixedError =
                ServiceStatusSnapshot.computeOverall okSection warnSection errSection okSection okSection okSection

            match mixedError with
            | UnhealthyBy ss ->
                Expect.contains ss ServiceStatusSnapshot.DriftSection "Drift named in UnhealthyBy"
                // Warn sections are not listed when an Error is present —
                // the pill is red, not yellow.
                Expect.isFalse
                    (List.contains ServiceStatusSnapshot.PreflightSection ss)
                    "Warn sections excluded from UnhealthyBy"
            | other -> failtestf "expected UnhealthyBy, got %A" other

            let allDisabled =
                ServiceStatusSnapshot.computeOverall
                    disabledSection
                    disabledSection
                    disabledSection
                    disabledSection
                    disabledSection
                    disabledSection

            Expect.equal allDisabled AllOk "every-section-disabled rolls up to AllOk (no active sections)"
        }

        test "Service-status-board-deps validator warns on all-disabled config" {
            let v =
                ServiceStatusBoardDepsValidator.ServiceStatusBoardDepsValidator(allDisabledConfig)
                :> ConfigValidation.IConfigValidator

            let result = v.Validate() |> Async.RunSynchronously

            match result with
            | ConfigValidation.Warning msg ->
                Expect.stringContains msg "JobScheduler" "names disabled job scheduler"
                Expect.stringContains msg "RateLimiter" "names disabled rate limiter"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Service-status-board-deps validator stays silent when any substrate is enabled" {
            let cfg = {
                allDisabledConfig with
                    JobScheduler = InProcessJobScheduler
            }

            let v =
                ServiceStatusBoardDepsValidator.ServiceStatusBoardDepsValidator(cfg)
                :> ConfigValidation.IConfigValidator

            let result = v.Validate() |> Async.RunSynchronously

            Expect.equal result ConfigValidation.Ok "any-substrate-enabled is Ok"
        }
    ]
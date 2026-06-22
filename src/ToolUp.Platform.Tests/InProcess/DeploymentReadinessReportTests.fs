module ToolUp.Platform.Tests.InProcess.DeploymentReadinessReportTests

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.HealthChecks

// ─── Phase 177 — Deployment-readiness scorecard tests ────────────────
//
// Two layers:
//   1. The pure verdict aggregation (`DeploymentReadiness.verdictOf` /
//      `summarise`) — the full truth-table, including the load-bearing
//      "a `NotComposed` source never inflates the verdict to `Ready`".
//   2. The server-side handler — the Platform-Admin gate (Anonymous /
//      non-admin → `Error`), the `NotComposed`-doesn't-fabricate-`Ready`
//      behaviour over a real DI graph, and a live-failing probe driving
//      `NotReady` with the offending name surfaced.
//
// `DeploymentReadiness` records are referenced qualified — the bare
// names `PreflightSummary` / `DriftSummary` / `HealthSummary` resolve to
// the Phase 9p.A ServiceStatusBoard abbreviations at `ToolUp.Platform`
// scope, so the scorecard's own shapes must be qualified to disambiguate.

// ─── Helpers ─────────────────────────────────────────────────────────

let private cleanPreflight: DeploymentReadiness.PreflightSummary = {
    Status = SourceStatus.Clean
    Total = 2
    Errors = []
    Warnings = []
}

let private cleanSmoke: DeploymentReadiness.SmokeSummary = {
    Status = SourceStatus.Clean
    Total = 3
    Failures = []
}

let private cleanDrift: DeploymentReadiness.DriftSummary = {
    Status = SourceStatus.Clean
    DriftEventCount = 0
}

let private cleanHealth: DeploymentReadiness.HealthSummary = {
    Status = SourceStatus.Clean
    Total = 4
    Unhealthy = []
    Degraded = []
}

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

let private healthProbe (name: string) (result: HealthResult) =
    { new IHealthCheck with
        member _.Name = name
        member _.Kind = HealthKind.Readiness
        member _.Timeout = TimeSpan.FromSeconds 5.0
        member _.Check() = async { return result }
    }

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 177 — DeploymentReadiness scorecard" [

        // ── Pure verdict truth-table ─────────────────────────────────

        test "verdictOf — all sources Clean ⇒ Ready" {
            let v =
                DeploymentReadiness.verdictOf [
                    SourceStatus.Clean
                    SourceStatus.Clean
                    SourceStatus.Clean
                    SourceStatus.Clean
                ]

            Expect.equal v ReadinessVerdict.Ready "all-clean ⇒ Ready"
        }

        test "verdictOf — a preflight Error ⇒ NotReady" {
            let v =
                DeploymentReadiness.verdictOf [
                    SourceStatus.Failed
                    SourceStatus.Clean
                    SourceStatus.Clean
                    SourceStatus.Clean
                ]

            Expect.equal v ReadinessVerdict.NotReady "any Failed ⇒ NotReady"
        }

        test "verdictOf — a failed smoke test ⇒ NotReady" {
            let v =
                DeploymentReadiness.verdictOf [
                    SourceStatus.Clean
                    SourceStatus.Failed
                    SourceStatus.NotComposed
                    SourceStatus.Clean
                ]

            Expect.equal v ReadinessVerdict.NotReady "failed smoke ⇒ NotReady"
        }

        test "verdictOf — an Unhealthy probe ⇒ NotReady" {
            let v =
                DeploymentReadiness.verdictOf [
                    SourceStatus.Clean
                    SourceStatus.Clean
                    SourceStatus.Clean
                    SourceStatus.Failed
                ]

            Expect.equal v ReadinessVerdict.NotReady "unhealthy probe ⇒ NotReady"
        }

        test "verdictOf — a preflight Warning (no hard failure) ⇒ DegradedReady" {
            let v =
                DeploymentReadiness.verdictOf [
                    SourceStatus.Warned
                    SourceStatus.Clean
                    SourceStatus.Clean
                    SourceStatus.Clean
                ]

            Expect.equal v ReadinessVerdict.DegradedReady "warning ⇒ DegradedReady"
        }

        test "verdictOf — drift detected (Warned) ⇒ DegradedReady" {
            let v =
                DeploymentReadiness.verdictOf [
                    SourceStatus.Clean
                    SourceStatus.Clean
                    SourceStatus.Warned
                    SourceStatus.Clean
                ]

            Expect.equal v ReadinessVerdict.DegradedReady "drift ⇒ DegradedReady"
        }

        test "verdictOf — a Degraded probe ⇒ DegradedReady" {
            let v =
                DeploymentReadiness.verdictOf [
                    SourceStatus.Clean
                    SourceStatus.Clean
                    SourceStatus.Clean
                    SourceStatus.Warned
                ]

            Expect.equal v ReadinessVerdict.DegradedReady "degraded probe ⇒ DegradedReady"
        }

        test "verdictOf — a hard failure takes precedence over a warning" {
            let v =
                DeploymentReadiness.verdictOf [
                    SourceStatus.Failed
                    SourceStatus.Warned
                    SourceStatus.NotComposed
                    SourceStatus.Clean
                ]

            Expect.equal v ReadinessVerdict.NotReady "Failed beats Warned"
        }

        test "verdictOf — all sources NotComposed ⇒ DegradedReady, never Ready" {
            let v =
                DeploymentReadiness.verdictOf [
                    SourceStatus.NotComposed
                    SourceStatus.NotComposed
                    SourceStatus.NotComposed
                    SourceStatus.NotComposed
                ]

            Expect.equal v ReadinessVerdict.DegradedReady "empty scorecard cannot attest Ready"
            Expect.notEqual v ReadinessVerdict.Ready "NotComposed must never inflate to Ready"
        }

        test "verdictOf — one Clean source amid NotComposed ⇒ Ready (NotComposed does not block)" {
            let v =
                DeploymentReadiness.verdictOf [
                    SourceStatus.Clean
                    SourceStatus.NotComposed
                    SourceStatus.NotComposed
                    SourceStatus.NotComposed
                ]

            Expect.equal v ReadinessVerdict.Ready "≥1 wired-and-green, none failed/warned ⇒ Ready"
        }

        // ── summarise wiring ─────────────────────────────────────────

        test "summarise — all-NotComposed sub-summaries never fabricate Ready" {
            let report =
                DeploymentReadiness.summarise
                    DateTime.UtcNow
                    DeploymentReadiness.PreflightSummary.notComposed
                    DeploymentReadiness.SmokeSummary.notComposed
                    DeploymentReadiness.DriftSummary.notComposed
                    DeploymentReadiness.HealthSummary.notComposed

            Expect.equal report.Verdict ReadinessVerdict.DegradedReady "no signal ⇒ DegradedReady"
            Expect.equal report.Preflight.Status SourceStatus.NotComposed "preflight NotComposed"
            Expect.equal report.SmokeTests.Status SourceStatus.NotComposed "smoke NotComposed"
            Expect.equal report.Drift.Status SourceStatus.NotComposed "drift NotComposed"
            Expect.equal report.Health.Status SourceStatus.NotComposed "health NotComposed"
        }

        test "summarise — threads the sub-summaries into the report unchanged" {
            let now = DateTime.UtcNow

            let report =
                DeploymentReadiness.summarise now cleanPreflight cleanSmoke cleanDrift cleanHealth

            Expect.equal report.Verdict ReadinessVerdict.Ready "all clean ⇒ Ready"
            Expect.equal report.GeneratedAt now "generatedAt threaded through"
            Expect.equal report.Preflight cleanPreflight "preflight threaded unchanged"
            Expect.equal report.Health cleanHealth "health threaded unchanged"
        }

        // ── Handler gate + DI behaviour ──────────────────────────────

        testCaseAsync "Non-admin caller receives 'platform admin role required'"
        <| async {
            let ctx = nonAdminContext ignore
            let api = DeploymentReadinessReport.deploymentReadinessApi ServerConfig.defaults ctx

            let! result = api.GetReadinessReport()

            match result with
            | Error msg -> Expect.equal msg "platform admin role required" "non-admin gate message"
            | Ok _ -> failtest "expected Error from non-admin caller"
        }

        testCaseAsync "Anonymous (no admin role) caller is denied the read"
        <| async {
            // Anonymous-mode deployments have no role to gate on — the
            // resolver yields an unrestricted Anonymous context with no
            // PlatformRole, which fails `canModifyPlatformConfig`.
            let ctx = nonAdminContext ignore
            let api = DeploymentReadinessReport.deploymentReadinessApi ServerConfig.defaults ctx

            let! result = api.GetReadinessReport()

            Expect.isError result "anonymous caller is denied"
        }

        testCaseAsync "Admin, no signals composed ⇒ DegradedReady with every source NotComposed"
        <| async {
            // ServerConfig.defaults: SmokeTest = No, ConfigDriftDetection
            // = No, and no IPreflightSnapshot / IHealthCheck registered in
            // DI — every source reads NotComposed. The verdict must be
            // DegradedReady (an empty scorecard, not an error, not Ready).
            let ctx = platformAdminContext ignore
            let api = DeploymentReadinessReport.deploymentReadinessApi ServerConfig.defaults ctx

            let! result = api.GetReadinessReport()

            match result with
            | Ok report ->
                Expect.equal
                    report.Preflight.Status
                    SourceStatus.NotComposed
                    "preflight NotComposed (no snapshot in DI)"

                Expect.equal report.SmokeTests.Status SourceStatus.NotComposed "smoke NotComposed (NoSmokeTest)"
                Expect.equal report.Drift.Status SourceStatus.NotComposed "drift NotComposed (NoConfigDriftDetection)"
                Expect.equal report.Health.Status SourceStatus.NotComposed "health NotComposed (no probes)"
                Expect.equal report.Verdict ReadinessVerdict.DegradedReady "no signal ⇒ DegradedReady, never Ready"
                Expect.notEqual report.Verdict ReadinessVerdict.Ready "NotComposed never fabricates Ready"
            | Error e -> failtestf "expected Ok, got Error %s" e
        }

        testCaseAsync "Admin, a live Unhealthy probe ⇒ NotReady naming the probe"
        <| async {
            let ctx =
                platformAdminContext (fun s ->
                    s.AddSingleton<IHealthCheck>(healthProbe "redis" (HealthResult.Unhealthy "connection refused"))
                    |> ignore

                    s.AddSingleton<IHealthCheck>(healthProbe "blob_storage" HealthResult.Healthy)
                    |> ignore)

            let api = DeploymentReadinessReport.deploymentReadinessApi ServerConfig.defaults ctx

            let! result = api.GetReadinessReport()

            match result with
            | Ok report ->
                Expect.equal report.Verdict ReadinessVerdict.NotReady "unhealthy probe ⇒ NotReady"
                Expect.equal report.Health.Status SourceStatus.Failed "health source Failed"
                Expect.contains report.Health.Unhealthy "redis" "failing probe named"
                Expect.equal report.Health.Total 2 "both probes counted"
            | Error e -> failtestf "expected Ok, got Error %s" e
        }

        testCaseAsync "Admin, a live Degraded probe ⇒ DegradedReady naming the probe"
        <| async {
            let ctx =
                platformAdminContext (fun s ->
                    s.AddSingleton<IHealthCheck>(healthProbe "oidc-auth" (HealthResult.Degraded "slow issuer"))
                    |> ignore)

            let api = DeploymentReadinessReport.deploymentReadinessApi ServerConfig.defaults ctx

            let! result = api.GetReadinessReport()

            match result with
            | Ok report ->
                Expect.equal report.Verdict ReadinessVerdict.DegradedReady "degraded probe ⇒ DegradedReady"
                Expect.contains report.Health.Degraded "oidc-auth" "degraded probe named"
            | Error e -> failtestf "expected Ok, got Error %s" e
        }
    ]
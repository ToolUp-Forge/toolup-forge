// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.HostingBaselineTests

// ─── Phase 771 — the hardening baseline, observed ────────────────────
//
// Four layers, each probed in both directions:
//
//   • the classification — every control observed on, then each one
//     switched off in turn, which must turn THAT control (and no other)
//     unenforced; the not-applicable shapes; a probe that faulted;
//   • the verdict — the floor under both profiles, and the Phase 657
//     refusal it carries;
//   • the report — the thirteenth section's three verdicts, its
//     not-composed reason, the not-proved statement carried as data, and
//     the boot record overlaid at report time;
//   • the probe — driven through a REAL composed ASP.NET Core pipeline, so
//     "observed, not intent" is tested against middleware rather than
//     against a stub: the same configuration with a header stripped by a
//     later middleware reads off, and a verified floor on a non-loopback
//     binding with HSTS off refuses the start naming the control.

open System
open System.Net
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.DeploymentVerification

// ─── synthetic observations ───────────────────────────────────────────

let private nonLoopback = [ "http://0.0.0.0:5000" ]

let private headersOn: Map<string, string> =
    Map.ofList [
        "content-security-policy", "default-src 'self'"
        "strict-transport-security", "max-age=31536000; includeSubDomains"
        "x-content-type-options", "nosniff"
        "x-frame-options", "DENY"
        "referrer-policy", "strict-origin-when-cross-origin"
    ]

let private response (headers: Map<string, string>) (scheme: string) : ProbeResponse = {
    ProbeStatus = 404
    ProbeHeaders = headers
    ProbeCookies = []
    ProbeScheme = scheme
    ProbeFault = None
}

/// Every control observed on, on a non-loopback plain-HTTP binding (so
/// HSTS and the forwarded-scheme control both apply).
let private allOn: HostingObservation = {
    ObservedBinding = nonLoopback
    DirectProbe = response headersOn "https"
    ProxiedProbe = response headersOn "https"
    UnknownKeyGuard = UnknownKeyGuardObservation.Refuses
    BoundManifest = Some "0123abcd"
    RateLimiters = [ "a global inbound rate limiter is composed" ]
}

let private withoutHeader (name: string) (observation: HostingObservation) = {
    observation with
        DirectProbe = {
            observation.DirectProbe with
                ProbeHeaders = observation.DirectProbe.ProbeHeaders |> Map.remove name
        }
}

let private withHeader (name: string) (value: string) (observation: HostingObservation) = {
    observation with
        DirectProbe = {
            observation.DirectProbe with
                ProbeHeaders = observation.DirectProbe.ProbeHeaders |> Map.add name value
        }
}

/// Each control, and an observation that switches exactly it off.
let private offCases: (HostingControl * HostingObservation) list = [
    HostingControl.ContentSecurityPolicy, allOn |> withoutHeader "content-security-policy"
    HostingControl.StrictTransportSecurity, allOn |> withoutHeader "strict-transport-security"
    HostingControl.ContentTypeOptions, allOn |> withoutHeader "x-content-type-options"
    HostingControl.FrameOptions, allOn |> withoutHeader "x-frame-options"
    HostingControl.ReferrerPolicy, allOn |> withoutHeader "referrer-policy"
    HostingControl.CorsCredentials,
    allOn
    |> withHeader "access-control-allow-origin" HostingBaseline.ProbeOrigin
    |> withHeader "access-control-allow-credentials" "true"
    HostingControl.UnknownConfigKeyRefusal,
    {
        allOn with
            UnknownKeyGuard = UnknownKeyGuardObservation.Warns
    }
    HostingControl.ConfigManifestBound, { allOn with BoundManifest = None }
    HostingControl.RateLimiting, { allOn with RateLimiters = [] }
    HostingControl.ForwardedSecureCookie,
    {
        allOn with
            ProxiedProbe = response headersOn "http"
    }
]

let private stateOf (baseline: HostingBaseline) (control: HostingControl) =
    baseline.BaselineControls
    |> List.find (fun c -> c.Control = control)
    |> _.ObservedState

let private isEnforced =
    function
    | HostingControlState.Enforced _ -> true
    | _ -> false

let private isUnenforced =
    function
    | HostingControlState.Unenforced _ -> true
    | _ -> false

let private isNotApplicable =
    function
    | HostingControlState.NotApplicable _ -> true
    | _ -> false

let private standard = HostingBaselinePolicy.informational

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

// ─── a real composed pipeline ─────────────────────────────────────────

/// A web app built the way compose builds one — the listen plan applied to
/// the builder, `configureServices` on its container, `configurePipeline`
/// on the built app — wrapped in the `IServerHost` compose returns. The
/// pipeline ends in a 404, which is what the probe's unknown path meets.
let private composedHost
    (bindAddress: string option)
    (configureServices: IServiceCollection -> unit)
    (configurePipeline: WebApplication -> unit)
    : WebApplication * IServerHost =
    let b = WebApplication.CreateBuilder()
    ComposeBootstrap.applyListenPlan b (ComposeBootstrap.listenPlanFor b bindAddress "0")
    configureServices b.Services
    let app = b.Build()
    configurePipeline app

    app.Run(fun ctx ->
        ctx.Response.StatusCode <- 404
        Task.CompletedTask)

    app, ServerHost.createWebHost app (fun () -> 0)

let private dispose (app: WebApplication) =
    (app :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()

let private observeHost (app: WebApplication) (host: IServerHost) (peer: IPAddress) =
    let binding = (ServerBinding.resolve app.Configuration None "0").Urls

    HostingBaseline.observe host.Invoke app.Services binding peer
    |> fun t -> t.GetAwaiter().GetResult()

let private hardenedConfig = {
    ServerConfig.defaults with
        RequireHttps = true
        SecurityHeaders = SecurityHeaders.productionDefaults
}

/// A middleware that strips a header another middleware stamps —
/// configuration says the header is on; the response says otherwise.
let private stripping (name: string) (app: WebApplication) =
    app.Use(
        Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
            ctx.Response.OnStarting(fun () ->
                ctx.Response.Headers.Remove name |> ignore
                Task.CompletedTask)

            next.Invoke ctx)
    )
    |> ignore

/// A consumer pre-middleware that reflects ANY origin with credentials —
/// the shape the compose-time CORS refusal cannot see, because it is not
/// `ServerConfig.Cors`.
let private reflectingCors (app: WebApplication) =
    app.Use(
        Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
            match ctx.Request.Headers.Origin |> Seq.tryHead with
            | Some origin ->
                ctx.Response.Headers["Access-Control-Allow-Origin"] <-
                    Microsoft.Extensions.Primitives.StringValues origin

                ctx.Response.Headers["Access-Control-Allow-Credentials"] <-
                    Microsoft.Extensions.Primitives.StringValues "true"
            | None -> ()

            next.Invoke ctx)
    )
    |> ignore

let tests =
    testList "Phase 771 — the hardening baseline" [

        testList "classification: each control observed on and off" [

            test "every control observed on reads enforced, and the verdict is Enforced" {
                let baseline = HostingBaseline.classify standard allOn

                for control in HostingControl.all do
                    Expect.isTrue (isEnforced (stateOf baseline control)) (HostingControl.label control)

                Expect.equal baseline.BaselineVerdict HostingBaselineVerdict.Enforced "verdict"
            }

            test "the closed list is ten controls, every one switched off by exactly one case below" {
                Expect.equal (HostingControl.all |> List.length) 10 "ten controls"

                Expect.equal
                    (offCases |> List.map fst |> List.sortBy HostingControl.label)
                    (HostingControl.all |> List.sortBy HostingControl.label)
                    "one off-case per control"
            }

            for control, observation in offCases do
                test $"{HostingControl.label control} observed off turns it — and only it — unenforced" {
                    let baseline = HostingBaseline.classify standard observation

                    Expect.isTrue (isUnenforced (stateOf baseline control)) "the switched control"

                    for other in HostingControl.all |> List.filter ((<>) control) do
                        Expect.isTrue
                            (isEnforced (stateOf baseline other))
                            (sprintf "%s stays enforced" (HostingControl.label other))

                    Expect.equal
                        baseline.BaselineVerdict
                        HostingBaselineVerdict.Unenforced
                        "standard profile: informational"
                }

            test "HSTS and the forwarded-scheme control do not apply on a loopback-only binding" {
                let baseline =
                    HostingBaseline.classify standard {
                        (allOn |> withoutHeader "strict-transport-security") with
                            ObservedBinding = [ "http://127.0.0.1:5000" ]
                            ProxiedProbe = response headersOn "http"
                    }

                Expect.isTrue (isNotApplicable (stateOf baseline HostingControl.StrictTransportSecurity)) "hsts"
                Expect.isTrue (isNotApplicable (stateOf baseline HostingControl.ForwardedSecureCookie)) "forwarded"
                Expect.equal baseline.BaselineVerdict HostingBaselineVerdict.Enforced "not-applicable is not off"
            }

            test "TLS terminating at the listener makes the forwarded-scheme control not applicable" {
                let baseline =
                    HostingBaseline.classify standard {
                        allOn with
                            ObservedBinding = [ "https://0.0.0.0:5001" ]
                            ProxiedProbe = response headersOn "http"
                    }

                Expect.isTrue (isNotApplicable (stateOf baseline HostingControl.ForwardedSecureCookie)) "forwarded"
            }

            test "a cookie emitted without Secure on the proxied HTTPS probe reads off" {
                let baseline =
                    HostingBaseline.classify standard {
                        allOn with
                            ProxiedProbe = {
                                response headersOn "https" with
                                    ProbeCookies = [ "session=abc; path=/; httponly" ]
                            }
                    }

                Expect.isTrue (isUnenforced (stateOf baseline HostingControl.ForwardedSecureCookie)) "insecure cookie"

                let secure =
                    HostingBaseline.classify standard {
                        allOn with
                            ProxiedProbe = {
                                response headersOn "https" with
                                    ProbeCookies = [ "session=abc; path=/; secure; httponly" ]
                            }
                    }

                Expect.isTrue (isEnforced (stateOf secure HostingControl.ForwardedSecureCookie)) "secure cookie"
            }

            test "framing governed by CSP frame-ancestors counts in place of X-Frame-Options" {
                let baseline =
                    HostingBaseline.classify
                        standard
                        (allOn
                         |> withoutHeader "x-frame-options"
                         |> withHeader "content-security-policy" "default-src 'self'; frame-ancestors 'none'")

                Expect.isTrue (isEnforced (stateOf baseline HostingControl.FrameOptions)) "frame-ancestors"
            }

            test "HSTS with max-age=0 is off; an uncredentialed grant to the probe origin is not" {
                let baseline =
                    HostingBaseline.classify
                        standard
                        (allOn
                         |> withHeader "strict-transport-security" "max-age=0"
                         |> withHeader "access-control-allow-origin" "*")

                Expect.isTrue (isUnenforced (stateOf baseline HostingControl.StrictTransportSecurity)) "max-age=0"
                Expect.isTrue (isEnforced (stateOf baseline HostingControl.CorsCredentials)) "no credentials"
            }

            test "a faulted probe reads every probe-borne control off, never on" {
                let faulted = {
                    response Map.empty "https" with
                        ProbeFault = Some "the probe request threw"
                }

                let baseline =
                    HostingBaseline.classify standard {
                        allOn with
                            DirectProbe = faulted
                            ProxiedProbe = faulted
                    }

                for control in
                    [
                        HostingControl.ContentSecurityPolicy
                        HostingControl.StrictTransportSecurity
                        HostingControl.ContentTypeOptions
                        HostingControl.FrameOptions
                        HostingControl.ReferrerPolicy
                        HostingControl.CorsCredentials
                        HostingControl.ForwardedSecureCookie
                    ] do
                    Expect.isTrue (isUnenforced (stateOf baseline control)) (HostingControl.label control)

                Expect.isTrue (isEnforced (stateOf baseline HostingControl.RateLimiting)) "container reads unaffected"
            }

            test "the unknown-key guard that did not run at this boot reads off" {
                let baseline =
                    HostingBaseline.classify standard {
                        allOn with
                            UnknownKeyGuard = UnknownKeyGuardObservation.NotRun
                    }

                Expect.isTrue (isUnenforced (stateOf baseline HostingControl.UnknownConfigKeyRefusal)) "not run"
            }
        ]

        testList "the floor, under both profiles" [

            let hstsOff = allOn |> withoutHeader "strict-transport-security"

            test "a verified profile whose floor names a control observed off is Refused" {
                let baseline =
                    HostingBaseline.classify
                        (HostingBaselinePolicy.verified [ HostingControl.StrictTransportSecurity ])
                        hstsOff

                Expect.equal baseline.BaselineVerdict HostingBaselineVerdict.Refused "verdict"

                match HostingBaseline.enforce baseline with
                | Error(HostingBaselineBelowFloor [ named ]) ->
                    Expect.stringContains named "strict-transport-security" "the refusal names the control"

                    Expect.stringContains
                        (CompositionProfileRefusal.describe (HostingBaselineBelowFloor [ named ]))
                        "strict-transport-security"
                        "the described refusal names it too"
                | other -> failtestf "expected HostingBaselineBelowFloor naming one control, got %A" other
            }

            test "the same floor under the standard profile refuses nothing" {
                let baseline =
                    HostingBaseline.classify
                        {
                            PolicyProfile = CompositionProfile.Standard
                            Floor = [ HostingControl.StrictTransportSecurity ]
                        }
                        hstsOff

                Expect.equal baseline.BaselineVerdict HostingBaselineVerdict.Unenforced "informational"
                Expect.equal (HostingBaseline.enforce baseline) (Ok()) "no refusal"
            }

            test "a verified floor that does not name the control off leaves the verdict Unenforced" {
                let baseline =
                    HostingBaseline.classify
                        (HostingBaselinePolicy.verified [ HostingControl.ContentSecurityPolicy ])
                        hstsOff

                Expect.equal baseline.BaselineVerdict HostingBaselineVerdict.Unenforced "not a floor control"
                Expect.equal (HostingBaseline.enforce baseline) (Ok()) "no refusal"
            }

            test "a verified floor fully met is Enforced" {
                let baseline =
                    HostingBaseline.classify (HostingBaselinePolicy.verified HostingControl.all) allOn

                Expect.equal baseline.BaselineVerdict HostingBaselineVerdict.Enforced "met"
                Expect.equal (HostingBaseline.enforce baseline) (Ok()) "no refusal"
            }

            test "the wildcard-CORS refusal still wins first" {
                // Compose raises the Phase 462 refusal before it registers
                // anything (`ComposeRuntimeServices.assertCorsCredentialsCompatible`,
                // called from `SDK.Server.compose`), so a composition that
                // conflicts never produces the `IServerHost` the baseline
                // probes. This replays `ServerApp.run`'s order — compose,
                // then observe — over that composition with a verified
                // floor naming the CORS control.
                let config = {
                    ServerConfig.defaults with
                        Cors =
                            Some {
                                Origins = [ "*" ]
                                Methods = [ "*" ]
                                Headers = [ "*" ]
                                AllowCredentials = true
                            }
                }

                let mutable baselineReached = false

                let boot () =
                    ComposeRuntimeServices.assertCorsCredentialsCompatible config silentLogger
                    baselineReached <- true

                    HostingBaseline.classify (HostingBaselinePolicy.verified [ HostingControl.CorsCredentials ]) allOn
                    |> HostingBaseline.enforce
                    |> ignore

                Expect.throwsT<ConfigValidatorAggregator.ConfigPreflightFailedException> boot "the CORS refusal fires"
                Expect.isFalse baselineReached "the baseline is never observed for a refused composition"
            }
        ]

        testList "the report section" [

            let sectionFor (baseline: HostingBaselineIntegrity option) =
                let report =
                    DeploymentVerificationEvidence.none
                    |> DeploymentVerificationEvidence.withHostingBaseline baseline
                    |> fun e -> DeploymentVerificationReport.buildReport e "probe" DateTime.UnixEpoch
                    |> Async.RunSynchronously

                report, report.Sections |> List.find (fun s -> s.Id = HostingBaselineSection)

            let integrityOf policy observation =
                HostingBaseline.classify policy observation |> HostingBaseline.toIntegrity

            test "no observation reads not-composed, and says why" {
                let _, section = sectionFor None
                Expect.equal (VerificationSectionVerdict.label section.Verdict) "not-composed" "verdict"

                Expect.stringContains
                    (VerificationSectionVerdict.detail section.Verdict)
                    "--verify-deployment"
                    "the reason names the entry that composes no pipeline"
            }

            test "Enforced is verified, Unenforced is observed, Refused is failed" {
                let _, enforced = sectionFor (Some(integrityOf standard allOn))
                let _, unenforced = sectionFor (Some(integrityOf standard (snd offCases.Head)))

                let _, refused =
                    sectionFor (
                        Some(
                            integrityOf
                                (HostingBaselinePolicy.verified [ HostingControl.ContentSecurityPolicy ])
                                (snd offCases.Head)
                        )
                    )

                Expect.equal (VerificationSectionVerdict.label enforced.Verdict) "verified" "enforced"
                Expect.equal (VerificationSectionVerdict.label unenforced.Verdict) "observed" "unenforced"
                Expect.equal (VerificationSectionVerdict.label refused.Verdict) "failed" "refused"

                Expect.stringContains
                    (VerificationSectionVerdict.detail refused.Verdict)
                    "content-security-policy"
                    "the failed verdict names the floor control"
            }

            test "a refused baseline makes the report non-affirmative" {
                let report, _ =
                    sectionFor (
                        Some(
                            integrityOf
                                (HostingBaselinePolicy.verified [ HostingControl.ContentSecurityPolicy ])
                                (snd offCases.Head)
                        )
                    )

                Expect.equal (exitCode report) 1 "exits non-zero"
                Expect.equal report.Outcome DeploymentVerificationOutcome.FailuresPresent "outcome"
            }

            test "there is one finding row per control, plus the binding" {
                let _, section = sectionFor (Some(integrityOf standard allOn))
                Expect.equal section.Findings.Length (HostingControl.all.Length + 1) "rows"

                for control in HostingControl.all do
                    Expect.isTrue
                        (section.Findings |> List.exists _.StartsWith(HostingControl.label control))
                        (HostingControl.label control)
            }

            test "a clean report carries the not-proved statement, narrowed" {
                let report, _ = sectionFor (Some(integrityOf standard allOn))

                let statement =
                    report.NotProved
                    |> List.tryFind (fun s -> s.Id = DeploymentVerificationReport.HostingBaselineNotProvedId)
                    |> Option.defaultWith (fun () -> failtest "the statement is missing")

                Expect.stringContains statement.Statement "proves header presence, not policy adequacy" "statement"
                Expect.isSome statement.Narrowing "narrowed when the section is composed"

                let bare, _ = sectionFor None

                let unnarrowed =
                    bare.NotProved
                    |> List.find (fun s -> s.Id = DeploymentVerificationReport.HostingBaselineNotProvedId)

                Expect.isNone unnarrowed.Narrowing "stands whole when nothing was observed"
            }

            test "the boot record is overlaid at report time" {
                let record = HostingBaselineRecord()
                record.Record(integrityOf standard allOn)

                let services =
                    ServiceCollection().AddSingleton<HostingBaselineRecord>(record).BuildServiceProvider()

                let evidence = DeploymentVerificationReport.resolveEvidence services

                Expect.equal
                    (DeploymentVerificationEvidence.hostingBaselineOf evidence)
                    record.Current
                    "the recorded observation reaches the evidence"

                let empty = HostingBaselineRecord()

                let none =
                    ServiceCollection().AddSingleton<HostingBaselineRecord>(empty).BuildServiceProvider()
                    |> DeploymentVerificationReport.resolveEvidence

                Expect.isNone (DeploymentVerificationEvidence.hostingBaselineOf none) "an empty record overlays nothing"
            }
        ]

        testList "the probe, through a real composed pipeline" [

            test "the hardened headers are observed on the response, not read from configuration" {
                let app, host =
                    composedHost (Some "0.0.0.0") ignore (fun app ->
                        app.UseMiddleware<SecurityHeadersMiddleware>(hardenedConfig) |> ignore)

                try
                    let baseline =
                        observeHost app host IPAddress.Loopback |> HostingBaseline.classify standard

                    for control in
                        [
                            HostingControl.ContentSecurityPolicy
                            HostingControl.StrictTransportSecurity
                            HostingControl.ContentTypeOptions
                            HostingControl.FrameOptions
                            HostingControl.ReferrerPolicy
                            HostingControl.CorsCredentials
                        ] do
                        Expect.isTrue (isEnforced (stateOf baseline control)) (HostingControl.label control)

                    Expect.equal baseline.BaselineBinding [ "http://0.0.0.0:0" ] "the binding the host resolves"
                finally
                    dispose app
            }

            test "the same configuration with the header stripped by another middleware reads off" {
                // Registered OUTSIDE the headers middleware: response-start
                // callbacks run in reverse registration order, so the outer
                // one's strip runs after the stamp — as it would on Kestrel.
                let app, host =
                    composedHost (Some "0.0.0.0") ignore (fun app ->
                        stripping "Strict-Transport-Security" app
                        app.UseMiddleware<SecurityHeadersMiddleware>(hardenedConfig) |> ignore)

                try
                    let baseline =
                        observeHost app host IPAddress.Loopback |> HostingBaseline.classify standard

                    Expect.isTrue (isUnenforced (stateOf baseline HostingControl.StrictTransportSecurity)) "stripped"
                    Expect.isTrue (isEnforced (stateOf baseline HostingControl.ContentTypeOptions)) "untouched"
                finally
                    dispose app
            }

            test "the stock floor without HTTPS sends no HSTS and no CSP" {
                let app, host =
                    composedHost (Some "0.0.0.0") ignore (fun app ->
                        app.UseMiddleware<SecurityHeadersMiddleware>(ServerConfig.defaults) |> ignore)

                try
                    let baseline =
                        observeHost app host IPAddress.Loopback |> HostingBaseline.classify standard

                    Expect.isTrue (isUnenforced (stateOf baseline HostingControl.StrictTransportSecurity)) "no hsts"
                    Expect.isTrue (isUnenforced (stateOf baseline HostingControl.ContentSecurityPolicy)) "no csp"
                    Expect.isTrue (isEnforced (stateOf baseline HostingControl.ContentTypeOptions)) "the 129d floor"
                finally
                    dispose app
            }

            test "a credentialed grant to an arbitrary origin is observed even when ServerConfig.Cors is unset" {
                let app, host = composedHost (Some "0.0.0.0") ignore reflectingCors

                try
                    let baseline =
                        observeHost app host IPAddress.Loopback |> HostingBaseline.classify standard

                    Expect.isTrue (isUnenforced (stateOf baseline HostingControl.CorsCredentials)) "reflected"
                finally
                    dispose app
            }

            test "forwarded-headers trust makes a proxied HTTPS request HTTPS; without it the Secure flag is lost" {
                let trusted = {
                    ServerConfig.defaults with
                        TrustForwardedHeaders = true
                        TrustedProxyCidrs = [ "10.0.0.0/8" ]
                }

                let peer = HostingBaseline.proxyPeerFor trusted
                Expect.isTrue (peer.ToString().StartsWith "10.") "the probe comes from inside the trusted network"

                let withTrust, withTrustHost =
                    composedHost (Some "0.0.0.0") ignore (fun app ->
                        app.UseForwardedHeaders(ConfigurePipeline.buildForwardedHeadersOptions trusted)
                        |> ignore)

                let without, withoutHost = composedHost (Some "0.0.0.0") ignore ignore

                try
                    let on =
                        observeHost withTrust withTrustHost peer |> HostingBaseline.classify standard

                    let off = observeHost without withoutHost peer |> HostingBaseline.classify standard

                    Expect.isTrue (isEnforced (stateOf on HostingControl.ForwardedSecureCookie)) "trusted"
                    Expect.isTrue (isUnenforced (stateOf off HostingControl.ForwardedSecureCookie)) "untrusted"

                    // From outside the trusted network the same pipeline
                    // honours nothing — the probe origin matters.
                    let outside =
                        observeHost withTrust withTrustHost (IPAddress.Parse "192.0.2.1")
                        |> HostingBaseline.classify standard

                    Expect.isTrue (isUnenforced (stateOf outside HostingControl.ForwardedSecureCookie)) "outside"
                finally
                    dispose withTrust
                    dispose without
            }

            test "a pipeline that throws is a faulted probe, not an exception" {
                let app, host =
                    composedHost (Some "0.0.0.0") ignore (fun app ->
                        app.Use(
                            Func<HttpContext, RequestDelegate, Task>(fun _ _ ->
                                raise (InvalidOperationException "boom"))
                        )
                        |> ignore)

                try
                    let observation = observeHost app host IPAddress.Loopback
                    Expect.isSome observation.DirectProbe.ProbeFault "fault recorded"

                    Expect.isTrue
                        (isUnenforced (
                            stateOf (HostingBaseline.classify standard observation) HostingControl.ContentSecurityPolicy
                        ))
                        "and reads off"
                finally
                    dispose app
            }
        ]

        testList "at boot" [

            test "nothing is probed when neither a policy nor the report asks for it (GP 13)" {
                let mutable probes = 0

                let app, host =
                    composedHost (Some "0.0.0.0") ignore (fun app ->
                        app.Use(
                            Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
                                probes <- probes + 1
                                next.Invoke ctx)
                        )
                        |> ignore)

                try
                    Expect.isNone (HostingBaseline.atBoot host ServerConfig.defaults) "no observation"
                    Expect.equal probes 0 "no probe request"
                finally
                    dispose app
            }

            test "a recorded observation reaches the record the report reads" {
                let record = HostingBaselineRecord()

                let app, host =
                    composedHost
                        (Some "0.0.0.0")
                        (fun services -> services.AddSingleton<HostingBaselineRecord>(record) |> ignore)
                        (fun app -> app.UseMiddleware<SecurityHeadersMiddleware>(hardenedConfig) |> ignore)

                try
                    let baseline = HostingBaseline.atBoot host hardenedConfig
                    Expect.isSome baseline "observed"
                    Expect.equal record.Current (baseline |> Option.map HostingBaseline.toIntegrity) "recorded"
                finally
                    dispose app
            }

            test "a verified composition with HSTS off on a non-loopback binding refuses to start, naming the control" {
                let app, host =
                    composedHost
                        (Some "0.0.0.0")
                        (fun services ->
                            services.AddSingleton<HostingBaselinePolicy>(
                                HostingBaselinePolicy.verified [ HostingControl.StrictTransportSecurity ]
                            )
                            |> ignore)
                        (fun app -> app.UseMiddleware<SecurityHeadersMiddleware>(ServerConfig.defaults) |> ignore)

                try
                    let refusal =
                        try
                            HostingBaseline.atBoot host ServerConfig.defaults |> ignore
                            failtest "the verified floor must refuse the start"
                        with ex ->
                            ex.Message

                    Expect.stringContains refusal "strict-transport-security" "names the control"
                    Expect.stringContains refusal "verified composition profile" "is the profile refusal"
                finally
                    dispose app
            }

            test "the same composition on a loopback binding starts: HSTS does not apply there" {
                let app, host =
                    composedHost
                        None
                        (fun services ->
                            services.AddSingleton<HostingBaselinePolicy>(
                                HostingBaselinePolicy.verified [ HostingControl.StrictTransportSecurity ]
                            )
                            |> ignore)
                        (fun app -> app.UseMiddleware<SecurityHeadersMiddleware>(ServerConfig.defaults) |> ignore)

                try
                    let baseline = HostingBaseline.atBoot host ServerConfig.defaults
                    Expect.isSome baseline "observed, not refused"

                    Expect.isTrue
                        (isNotApplicable (stateOf baseline.Value HostingControl.StrictTransportSecurity))
                        "not applicable on loopback"
                finally
                    dispose app
            }
        ]
    ]
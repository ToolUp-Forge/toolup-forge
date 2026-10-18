// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.IO
open System.Net
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options

// ─── Phase 771 — the hardening baseline, observed ────────────────────
//
// The pieces of a hardened host already ship as separate mechanisms: the
// CSP middleware and its contributor aggregation (Phase 9j), the
// security-headers floor (Phase 129d), the credentials-with-wildcard CORS
// refusal (Phase 462), the unknown-config-key guard (Phase 695), the
// configuration manifest (Phase 696), rate limiting (Phases 9 and 56) and
// forwarded-headers trust (Phase 325). Nothing declared them as ONE
// baseline, and nothing proved at boot which of them this process actually
// serves. This file is that proof, and its one rule is that every control
// is OBSERVED, never read from `ServerConfig`:
//
//   * the header controls are read off the response to a probe request
//     driven through the composed pipeline (`IServerHost.Invoke`, the seam
//     serverless hosts already use), so a header configured on and
//     stripped, overridden or never mounted reads as off;
//   * the cookie `Secure` control sends the probe the way a TLS-terminating
//     proxy would — plain HTTP from the proxy's address carrying
//     `X-Forwarded-Proto: https` — and reads the scheme the pipeline
//     resolved, which is the one input every SDK cookie site's `Secure`
//     flag follows (`Request.IsHttps`), plus the flag on any cookie the
//     response actually set;
//   * the remaining controls are read off what the container holds and what
//     the preflight recorded: the unknown-key guard's outcome at THIS boot,
//     the installed manifest, the rate limiter the pipeline resolves.
//
// ServerConfig is consulted for exactly one thing: the address the proxied
// probe originates from, which must sit inside the declared trusted-proxy
// networks for forwarded-headers trust to apply to it at all. That chooses
// where the probe comes FROM; it never decides what the control reads.
//
// **Where it runs.** `ServerApp.run` calls `atBoot` after `compose` returns
// and before `RunBlocking` — after composition, before serve — and only
// when something will read the result (the verification route is mounted,
// or a `HostingBaselinePolicy` is declared), so a deployment asking for
// neither sends no probe (GP 13). A root driving the host itself (a
// serverless adapter) calls `atBoot` on the same terms.
//
// **What it refuses.** Nothing, under `CompositionProfile.Standard`: the
// section is informational (GP 11). Under `Verified`, a control the declared
// floor names and the probe observed off refuses the start through the
// Phase 657 profile refusal (`HostingBaselineBelowFloor`). The config
// preflight runs inside `compose`, so its refusals — the wildcard-CORS one
// among them — always win first.
//
// Compiles after `BootVerificationPreflight.fs` (the policy names a
// `CompositionProfile`) and before `ServerApp.fs` (whose `run` calls it);
// the report reads it through the tier-neutral mirror in
// `DeploymentVerificationReport.fs`, the way it reads the egress handler.

/// The closed list of hardening-baseline controls (Phase 771). Closed on
/// purpose: a baseline is a statement about a fixed set, and a control added
/// later is a new phase with its own observation, not an open slot.
[<RequireQualifiedAccess>]
type HostingControl =
    /// A non-empty `Content-Security-Policy` header on the probe response.
    | ContentSecurityPolicy
    /// `Strict-Transport-Security` with a positive `max-age` on the probe
    /// response. Not applicable on a loopback-only binding.
    | StrictTransportSecurity
    /// `X-Content-Type-Options: nosniff`.
    | ContentTypeOptions
    /// `X-Frame-Options: DENY | SAMEORIGIN`, or a CSP `frame-ancestors`
    /// directive that governs framing in its place.
    | FrameOptions
    /// A `Referrer-Policy` other than `unsafe-url`.
    | ReferrerPolicy
    /// No credentialed CORS grant to an arbitrary origin.
    | CorsCredentials
    /// The Phase 695 unknown-config-key guard ran at this boot in refusing
    /// (strict) mode.
    | UnknownConfigKeyRefusal
    /// A Phase 696 configuration manifest is bound.
    | ConfigManifestBound
    /// A rate limiter is composed.
    | RateLimiting
    /// Behind a TLS-terminating proxy, a proxied HTTPS request reaches the
    /// pipeline as HTTPS, so the cookie `Secure` flag is emitted. Not
    /// applicable on a loopback-only binding or where TLS terminates at this
    /// listener.
    | ForwardedSecureCookie

[<RequireQualifiedAccess>]
module HostingControl =

    /// Every control, in the order the report lists them.
    let all: HostingControl list = [
        HostingControl.ContentSecurityPolicy
        HostingControl.StrictTransportSecurity
        HostingControl.ContentTypeOptions
        HostingControl.FrameOptions
        HostingControl.ReferrerPolicy
        HostingControl.CorsCredentials
        HostingControl.UnknownConfigKeyRefusal
        HostingControl.ConfigManifestBound
        HostingControl.RateLimiting
        HostingControl.ForwardedSecureCookie
    ]

    /// Stable lowercase label — the report row's id and the refusal's name
    /// for the control. Never localised.
    let label =
        function
        | HostingControl.ContentSecurityPolicy -> "content-security-policy"
        | HostingControl.StrictTransportSecurity -> "strict-transport-security"
        | HostingControl.ContentTypeOptions -> "x-content-type-options"
        | HostingControl.FrameOptions -> "x-frame-options"
        | HostingControl.ReferrerPolicy -> "referrer-policy"
        | HostingControl.CorsCredentials -> "cors-credentials"
        | HostingControl.UnknownConfigKeyRefusal -> "unknown-config-key-refusal"
        | HostingControl.ConfigManifestBound -> "config-manifest-bound"
        | HostingControl.RateLimiting -> "rate-limiting"
        | HostingControl.ForwardedSecureCookie -> "forwarded-secure-cookie"

/// One control as observed (Phase 771).
type BaselineControl = {
    Control: HostingControl
    /// What the composed pipeline was observed to do.
    ObservedState: HostingControlState
    /// Whether the declared floor names this control.
    InFloor: bool
}

/// The floor a deployment declares (Phase 771): which controls must be
/// observed on. Under `CompositionProfile.Verified` a floor control observed
/// off refuses the start; under `Standard` the floor only marks rows in the
/// report (GP 11).
///
/// A new record rather than a `ServerConfig` field, for the reason
/// `BootVerificationOptions` gives: widening that record retypes its
/// constructor for every consumer, for a feature no existing consumer
/// configures.
type HostingBaselinePolicy = {
    PolicyProfile: CompositionProfile
    Floor: HostingControl list
}

[<RequireQualifiedAccess>]
module HostingBaselinePolicy =

    /// Informational: no floor, the standard profile. What a deployment that
    /// mounts the verification route without declaring a policy is judged by.
    let informational: HostingBaselinePolicy = {
        PolicyProfile = CompositionProfile.Standard
        Floor = []
    }

    /// The verified profile with `floor` as its declared floor.
    let verified (floor: HostingControl list) : HostingBaselinePolicy = {
        PolicyProfile = CompositionProfile.Verified
        Floor = floor
    }

/// What one probe request came back with (Phase 771).
type ProbeResponse = {
    ProbeStatus: int
    /// Response headers, keyed by lower-cased name, multiple values joined
    /// with `, `.
    ProbeHeaders: Map<string, string>
    /// Every `Set-Cookie` value the response carried.
    ProbeCookies: string list
    /// The scheme the pipeline had resolved for the request by the time it
    /// completed — after forwarded-headers processing, when composed.
    ProbeScheme: string
    /// Why the probe produced no usable response, when it did not.
    ProbeFault: string option
}

/// How the Phase 695 guard fared at this boot.
[<RequireQualifiedAccess>]
type UnknownKeyGuardObservation =
    /// It did not run: preflight was skipped, or nothing composed it.
    | NotRun
    /// It ran, and only ever warns.
    | Warns
    /// It ran in strict mode, so an unknown key refuses the boot.
    | Refuses

/// Everything one boot's observation read (Phase 771). Pure data, so the
/// classification below is testable without a pipeline.
type HostingObservation = {
    /// The listen URLs, as the host configuration resolves them.
    ObservedBinding: string list
    /// The probe as a direct HTTPS request from an arbitrary origin.
    DirectProbe: ProbeResponse
    /// The probe as a TLS-terminating proxy forwards one.
    ProxiedProbe: ProbeResponse
    UnknownKeyGuard: UnknownKeyGuardObservation
    /// The bound manifest's SHA-256, when one is bound.
    BoundManifest: string option
    /// The rate limiting the container composes, named. Empty is none.
    RateLimiters: string list
}

/// The hardening baseline (Phase 771): every control as observed, the
/// policy it was judged against, and the verdict.
type HostingBaseline = {
    BaselinePolicy: HostingBaselinePolicy
    BaselineBinding: string list
    BaselineControls: BaselineControl list
    BaselineVerdict: HostingBaselineVerdict
}

[<RequireQualifiedAccess>]
module HostingBaseline =

    /// The path the probe requests. Under `/.well-known/` and naming nothing
    /// a deployment serves, so what answers it is whatever the pipeline does
    /// with an unknown path — which still passes every header middleware.
    [<Literal>]
    let ProbePath = "/.well-known/hardening-baseline-probe"

    /// The origin the direct probe claims. `.invalid` is reserved (RFC 2606),
    /// so no CORS policy can have listed it on purpose: a grant to it is a
    /// grant to any origin.
    [<Literal>]
    let ProbeOrigin = "https://hardening-probe.invalid"

    /// The address the proxied probe is forwarded for (RFC 5737 TEST-NET-3).
    [<Literal>]
    let ProbeClientAddress = "203.0.113.7"

    /// How long one probe may take before it is recorded as a fault.
    let ProbeTimeout = TimeSpan.FromSeconds 10.0

    // ── the probe ────────────────────────────────────────────────────────

    let private faulted (scheme: string) (fault: string) : ProbeResponse = {
        ProbeStatus = 0
        ProbeHeaders = Map.empty
        ProbeCookies = []
        ProbeScheme = scheme
        ProbeFault = Some fault
    }

    /// Drive one GET through `invoke` (the composed pipeline) and read what
    /// came back. Total: a pipeline that throws or stalls is a `ProbeFault`,
    /// never an exception out of here.
    let probe
        (invoke: HttpContext -> Task)
        (services: IServiceProvider)
        (scheme: string)
        (peer: IPAddress)
        (headers: (string * string) list)
        : Task<ProbeResponse> =
        task {
            let features = FeatureCollection()

            let request = HttpRequestFeature()
            request.Protocol <- "HTTP/1.1"
            request.Method <- HttpMethods.Get
            request.Scheme <- scheme
            request.PathBase <- ""
            request.Path <- ProbePath
            request.QueryString <- ""
            request.RawTarget <- ProbePath
            let requestHeaders = HeaderDictionary()
            requestHeaders["Host"] <- Microsoft.Extensions.Primitives.StringValues "localhost"

            for name, value in headers do
                requestHeaders[name] <- Microsoft.Extensions.Primitives.StringValues value

            request.Headers <- requestHeaders
            request.Body <- Stream.Null
            features.Set<IHttpRequestFeature> request

            // The batch dispatcher's response feature: it keeps the
            // `OnStarting` / `OnCompleted` callbacks a bare
            // `DefaultHttpContext` drops — without them every header control
            // would read as off — and fires them in reverse registration
            // order, as a server does.
            let response = ToolUp.Remoting.Giraffe.RemotingBatch.ElementResponseFeature()
            features.Set<IHttpResponseFeature> response
            use body = new MemoryStream()
            let bodyFeature = StreamResponseBodyFeature(body)
            features.Set<IHttpResponseBodyFeature> bodyFeature

            let connection = HttpConnectionFeature()
            connection.RemoteIpAddress <- peer
            connection.LocalIpAddress <- IPAddress.Loopback
            connection.ConnectionId <- "hardening-baseline-probe"
            features.Set<IHttpConnectionFeature> connection

            use timeout = new CancellationTokenSource(ProbeTimeout)
            features.Set<IHttpRequestLifetimeFeature>(HttpRequestLifetimeFeature(RequestAborted = timeout.Token))
            features.Set<IItemsFeature>(ItemsFeature())
            features.Set<IQueryFeature>(QueryFeature(features))
            features.Set<IRequestCookiesFeature>(RequestCookiesFeature(features))
            features.Set<IResponseCookiesFeature>(ResponseCookiesFeature(features))

            use scope = services.CreateAsyncScope()
            features.Set<IServiceProvidersFeature>(ServiceProvidersFeature(RequestServices = scope.ServiceProvider))

            let context = DefaultHttpContext(features)

            let! fault = task {
                try
                    let run = invoke context
                    let! winner = Task.WhenAny(run, Task.Delay(ProbeTimeout))

                    if Object.ReferenceEquals(winner, run) then
                        do! run
                        do! response.Start()
                        return None
                    else
                        return Some(sprintf "the probe did not complete within %.0f s" ProbeTimeout.TotalSeconds)
                with ex ->
                    return Some(sprintf "the probe request threw %s: %s" (ex.GetType().Name) ex.Message)
            }

            match fault with
            | Some reason -> return faulted context.Request.Scheme reason
            | None ->
                do! bodyFeature.CompleteAsync()
                do! response.Complete()

                let read =
                    response.Headers
                    |> Seq.filter (fun h ->
                        not (String.Equals(h.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase)))
                    |> Seq.map (fun h -> h.Key.ToLowerInvariant(), String.Join(", ", h.Value.ToArray()))
                    |> Map.ofSeq

                return {
                    ProbeStatus = response.StatusCode
                    ProbeHeaders = read
                    ProbeCookies = response.Headers.SetCookie.ToArray() |> List.ofArray
                    ProbeScheme = context.Request.Scheme
                    ProbeFault = None
                }
        }

    // ── classification ───────────────────────────────────────────────────

    let private isLoopbackOnly (binding: string list) =
        not binding.IsEmpty && binding |> List.forall ServerBinding.isLoopbackUrl

    let private terminatesTlsHere (binding: string list) =
        binding
        |> List.exists (fun url -> url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))

    let private header (name: string) (response: ProbeResponse) =
        response.ProbeHeaders
        |> Map.tryFind name
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let private onProbe (response: ProbeResponse) (check: ProbeResponse -> HostingControlState) =
        match response.ProbeFault with
        | Some fault -> HostingControlState.Unenforced(sprintf "unobserved: %s" fault)
        | None -> check response

    let private maxAge (hsts: string) : int64 option =
        hsts.Split(';')
        |> Array.map _.Trim()
        |> Array.tryPick (fun directive ->
            if directive.StartsWith("max-age=", StringComparison.OrdinalIgnoreCase) then
                match Int64.TryParse(directive.Substring("max-age=".Length).Trim('"')) with
                | true, seconds -> Some seconds
                | _ -> None
            else
                None)

    let private classifyControl (observation: HostingObservation) (control: HostingControl) : HostingControlState =
        let direct = observation.DirectProbe
        let binding = observation.ObservedBinding

        match control with
        | HostingControl.ContentSecurityPolicy ->
            onProbe direct (fun r ->
                match header "content-security-policy" r with
                | Some csp ->
                    HostingControlState.Enforced(sprintf "Content-Security-Policy is sent (%d chars)" csp.Length)
                | None ->
                    HostingControlState.Unenforced
                        "the probe response carried no Content-Security-Policy header; compose security hardening or set one in the security-headers map")
        | HostingControl.StrictTransportSecurity ->
            if isLoopbackOnly binding then
                HostingControlState.NotApplicable
                    "every listen URL is loopback, so no browser reaches this listener to be told to stay on HTTPS"
            else
                onProbe direct (fun r ->
                    match header "strict-transport-security" r with
                    | Some hsts ->
                        match maxAge hsts with
                        | Some seconds when seconds > 0L ->
                            HostingControlState.Enforced(sprintf "Strict-Transport-Security: %s" hsts)
                        | _ ->
                            HostingControlState.Unenforced(
                                sprintf "Strict-Transport-Security is sent with no positive max-age: %s" hsts
                            )
                    | None ->
                        HostingControlState.Unenforced
                            "an HTTPS probe on a non-loopback binding carried no Strict-Transport-Security header; require HTTPS, or set the header in the security-headers map")
        | HostingControl.ContentTypeOptions ->
            onProbe direct (fun r ->
                match header "x-content-type-options" r with
                | Some value when String.Equals(value.Trim(), "nosniff", StringComparison.OrdinalIgnoreCase) ->
                    HostingControlState.Enforced "X-Content-Type-Options: nosniff"
                | Some value ->
                    HostingControlState.Unenforced(sprintf "X-Content-Type-Options is '%s', not nosniff" value)
                | None -> HostingControlState.Unenforced "the probe response carried no X-Content-Type-Options header")
        | HostingControl.FrameOptions ->
            onProbe direct (fun r ->
                let framing =
                    header "content-security-policy" r
                    |> Option.filter (fun csp -> csp.Contains("frame-ancestors", StringComparison.OrdinalIgnoreCase))

                match header "x-frame-options" r, framing with
                | Some value, _ when
                    [ "DENY"; "SAMEORIGIN" ]
                    |> List.exists (fun v -> String.Equals(value.Trim(), v, StringComparison.OrdinalIgnoreCase))
                    ->
                    HostingControlState.Enforced(sprintf "X-Frame-Options: %s" value)
                | _, Some _ -> HostingControlState.Enforced "framing is governed by the CSP frame-ancestors directive"
                | Some value, None ->
                    HostingControlState.Unenforced(sprintf "X-Frame-Options is '%s', which no browser enforces" value)
                | None, None ->
                    HostingControlState.Unenforced
                        "the probe response carried neither X-Frame-Options nor a CSP frame-ancestors directive")
        | HostingControl.ReferrerPolicy ->
            onProbe direct (fun r ->
                match header "referrer-policy" r with
                | Some value when String.Equals(value.Trim(), "unsafe-url", StringComparison.OrdinalIgnoreCase) ->
                    HostingControlState.Unenforced
                        "Referrer-Policy is unsafe-url, which sends the full URL cross-origin"
                | Some value -> HostingControlState.Enforced(sprintf "Referrer-Policy: %s" value)
                | None -> HostingControlState.Unenforced "the probe response carried no Referrer-Policy header")
        | HostingControl.CorsCredentials ->
            onProbe direct (fun r ->
                let credentialed =
                    header "access-control-allow-credentials" r
                    |> Option.exists (fun v -> String.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase))

                match header "access-control-allow-origin" r with
                | Some origin when credentialed && (origin = "*" || origin = ProbeOrigin) ->
                    HostingControlState.Unenforced(
                        sprintf
                            "a request from the arbitrary origin %s was granted credentialed CORS access (Access-Control-Allow-Origin: %s)"
                            ProbeOrigin
                            origin
                    )
                | Some origin ->
                    HostingControlState.Enforced(
                        sprintf
                            "the arbitrary origin is granted CORS access without credentials (Access-Control-Allow-Origin: %s)"
                            origin
                    )
                | None -> HostingControlState.Enforced "no CORS grant was made to an arbitrary origin")
        | HostingControl.UnknownConfigKeyRefusal ->
            match observation.UnknownKeyGuard with
            | UnknownKeyGuardObservation.Refuses ->
                HostingControlState.Enforced
                    "the unknown-config-key guard ran at this boot in strict mode, so an unrecognised TOOLUP_ variable refuses the start"
            | UnknownKeyGuardObservation.Warns ->
                HostingControlState.Unenforced(
                    sprintf
                        "the unknown-config-key guard ran at this boot and only warns; set %s=1 once the environment is curated"
                        ConfigKeys.Names.strictConfig
                )
            | UnknownKeyGuardObservation.NotRun ->
                HostingControlState.Unenforced
                    "the unknown-config-key guard did not run at this boot (the external-probe class of the preflight was skipped, or nothing composed it)"
        | HostingControl.ConfigManifestBound ->
            match observation.BoundManifest with
            | Some hash -> HostingControlState.Enforced(sprintf "a configuration manifest is bound (sha256 %s)" hash)
            | None ->
                HostingControlState.Unenforced
                    "no configuration manifest is bound, so there is no declared configuration to conform to"
        | HostingControl.RateLimiting ->
            match observation.RateLimiters with
            | [] -> HostingControlState.Unenforced "the container composes no inbound rate limiter"
            | limiters -> HostingControlState.Enforced(String.concat "; " limiters)
        | HostingControl.ForwardedSecureCookie ->
            if isLoopbackOnly binding then
                HostingControlState.NotApplicable
                    "every listen URL is loopback, so no proxy hop sits between a browser and this listener"
            elif terminatesTlsHere binding then
                HostingControlState.NotApplicable
                    "TLS terminates at this listener, so Request.IsHttps is the connection's own and needs no forwarded scheme"
            else
                onProbe observation.ProxiedProbe (fun r ->
                    let insecure =
                        r.ProbeCookies
                        |> List.filter (fun cookie ->
                            not (
                                cookie.Split(';')
                                |> Array.exists (fun attr ->
                                    String.Equals(attr.Trim(), "secure", StringComparison.OrdinalIgnoreCase))
                            ))

                    if not (String.Equals(r.ProbeScheme, "https", StringComparison.OrdinalIgnoreCase)) then
                        HostingControlState.Unenforced(
                            sprintf
                                "a request forwarded with X-Forwarded-Proto: https reached the pipeline as %s, so every SDK cookie's Secure flag (it follows Request.IsHttps) is dropped behind a TLS-terminating proxy; trust forwarded headers from the proxy's network"
                                r.ProbeScheme
                        )
                    elif not insecure.IsEmpty then
                        HostingControlState.Unenforced(
                            sprintf
                                "the proxied HTTPS probe was answered with %d cookie(s) without Secure"
                                insecure.Length
                        )
                    elif r.ProbeCookies.IsEmpty then
                        HostingControlState.Enforced
                            "a request forwarded with X-Forwarded-Proto: https reached the pipeline as https, which is the input every SDK cookie's Secure flag follows"
                    else
                        HostingControlState.Enforced(
                            sprintf
                                "a request forwarded with X-Forwarded-Proto: https reached the pipeline as https, and the %d cookie(s) it set carry Secure"
                                r.ProbeCookies.Length
                        ))

    /// Judge an observation against a policy. Pure and total.
    ///
    /// `Refused` needs both halves: the verified profile AND a control its
    /// floor names observed off. A floor under the standard profile marks
    /// rows and refuses nothing (GP 11); a control off that the floor does
    /// not name leaves the verdict `Unenforced` under either profile.
    let classify (policy: HostingBaselinePolicy) (observation: HostingObservation) : HostingBaseline =
        let controls =
            HostingControl.all
            |> List.map (fun control -> {
                Control = control
                ObservedState = classifyControl observation control
                InFloor = policy.Floor |> List.contains control
            })

        let off =
            controls
            |> List.filter (fun c ->
                match c.ObservedState with
                | HostingControlState.Unenforced _ -> true
                | _ -> false)

        let verdict =
            if off.IsEmpty then
                HostingBaselineVerdict.Enforced
            elif
                policy.PolicyProfile = CompositionProfile.Verified
                && off |> List.exists _.InFloor
            then
                HostingBaselineVerdict.Refused
            else
                HostingBaselineVerdict.Unenforced

        {
            BaselinePolicy = policy
            BaselineBinding = observation.ObservedBinding
            BaselineControls = controls
            BaselineVerdict = verdict
        }

    /// The Phase 657 refusal a `Refused` baseline carries: every floor
    /// control observed off, named. `Ok` for any other verdict.
    let enforce (baseline: HostingBaseline) : Result<unit, CompositionProfileRefusal> =
        match baseline.BaselineVerdict with
        | HostingBaselineVerdict.Refused ->
            baseline.BaselineControls
            |> List.choose (fun c ->
                match c.ObservedState with
                | HostingControlState.Unenforced detail when c.InFloor ->
                    Some(sprintf "%s (%s)" (HostingControl.label c.Control) detail)
                | _ -> None)
            |> HostingBaselineBelowFloor
            |> Error
        | HostingBaselineVerdict.Enforced
        | HostingBaselineVerdict.Unenforced -> Ok()

    /// The report's mirror of a baseline — the mapping beside the
    /// substrate, per the 693 tier-order note.
    let toIntegrity (baseline: HostingBaseline) : HostingBaselineIntegrity = {
        HostingProfile = CompositionProfile.label baseline.BaselinePolicy.PolicyProfile
        HostingVerdict = baseline.BaselineVerdict
        HostingBinding = baseline.BaselineBinding
        HostingControls =
            baseline.BaselineControls
            |> List.map (fun c -> {
                HostingControlId = HostingControl.label c.Control
                HostingInFloor = c.InFloor
                HostingObserved = c.ObservedState
            })
    }

    /// One line for the boot log.
    let describe (baseline: HostingBaseline) : string =
        let count predicate =
            baseline.BaselineControls
            |> List.filter (fun c -> predicate c.ObservedState)
            |> List.length

        let off =
            baseline.BaselineControls
            |> List.filter (fun c ->
                match c.ObservedState with
                | HostingControlState.Unenforced _ -> true
                | _ -> false)
            |> List.map (fun c -> HostingControl.label c.Control)

        sprintf
            "[Phase 771] hardening baseline (%s profile): %d enforced, %d unenforced, %d not applicable%s"
            (CompositionProfile.label baseline.BaselinePolicy.PolicyProfile)
            (count (function
                | HostingControlState.Enforced _ -> true
                | _ -> false))
            off.Length
            (count (function
                | HostingControlState.NotApplicable _ -> true
                | _ -> false))
            (if off.IsEmpty then
                 ""
             else
                 sprintf " — off: %s" (String.concat ", " off))

    // ── reading the composition ──────────────────────────────────────────

    /// The single container read this file makes. Kept in one place so the
    /// file holds one resolution site.
    let private tryResolve<'T when 'T: not struct> (services: IServiceProvider) : 'T option =
        match services.GetService(typeof<'T>) with
        | :? 'T as resolved -> Some resolved
        | _ -> None

    /// How the unknown-key guard fared at THIS boot: read off the preflight's
    /// own record of what ran, and the strict setting the guard itself reads.
    let private unknownKeyGuard (services: IServiceProvider) : UnknownKeyGuardObservation =
        let ran =
            tryResolve<ConfigValidatorAggregator.IPreflightSnapshot> services
            |> Option.exists (fun snapshot ->
                snapshot.LastRun
                |> List.exists (fun outcome -> outcome.Name = UnknownConfigKeyValidator.UnknownConfigKeyValidator.Name))

        if not ran then
            UnknownKeyGuardObservation.NotRun
        elif UnknownConfigKeyValidator.strictModeEnabled () then
            UnknownKeyGuardObservation.Refuses
        else
            UnknownKeyGuardObservation.Warns

    /// The inbound rate limiting the container composes, named.
    let private rateLimiters (services: IServiceProvider) : string list = [
        match tryResolve<IOptions<RateLimiterOptions>> services with
        | Some options when not (isNull options.Value.GlobalLimiter) ->
            yield "a global inbound rate limiter is composed"
        | _ -> ()

        match tryResolve<IRateLimitStore> services with
        | Some _ -> yield "a per-route rate-limit store is composed"
        | None -> ()
    ]

    /// The address the proxied probe comes from: inside the first declared
    /// trusted-proxy network when there is one (forwarded-headers trust is
    /// scoped to those networks), else loopback.
    let proxyPeerFor (config: ServerConfig) : IPAddress =
        match ForwardedHeadersTrustValidator.Cidr.parseAll config.TrustedProxyCidrs with
        | Ok(network :: _) -> network.BaseAddress
        | _ -> IPAddress.Loopback

    /// Observe the composition: both probes, then the container reads.
    let observe
        (invoke: HttpContext -> Task)
        (services: IServiceProvider)
        (binding: string list)
        (proxyPeer: IPAddress)
        : Task<HostingObservation> =
        task {
            let! direct = probe invoke services "https" IPAddress.Loopback [ "Origin", ProbeOrigin ]

            let! proxied =
                probe invoke services "http" proxyPeer [
                    "X-Forwarded-Proto", "https"
                    "X-Forwarded-For", ProbeClientAddress
                ]

            return {
                ObservedBinding = binding
                DirectProbe = direct
                ProxiedProbe = proxied
                UnknownKeyGuard = unknownKeyGuard services
                BoundManifest = ConfigResolution.snapshot () |> Option.map _.Hash
                RateLimiters = rateLimiters services
            }
        }

    /// Observe and judge a composed host at boot — after composition, before
    /// serve. `None` (and no probe sent) when the host serves no HTTP, or
    /// when nothing will read the result: no `HostingBaselinePolicy` and no
    /// `HostingBaselineRecord` registered (GP 13). Records the result for the
    /// report, logs one line, and under the verified profile REFUSES the
    /// start (`failwith` the Phase 657 refusal) when a floor control was
    /// observed off.
    let atBoot (host: IServerHost) (config: ServerConfig) : HostingBaseline option =
        match host.App with
        | None -> None
        | Some app ->
            let services = host.Host.Services
            let declared = tryResolve<HostingBaselinePolicy> services
            let record = tryResolve<HostingBaselineRecord> services

            if declared.IsNone && record.IsNone then
                None
            else
                let policy = declared |> Option.defaultValue HostingBaselinePolicy.informational
                let binding = (ServerBinding.resolve app.Configuration None "0").Urls

                let observation =
                    (observe host.Invoke services binding (proxyPeerFor config)).GetAwaiter().GetResult()

                let baseline = classify policy observation
                record |> Option.iter (fun r -> r.Record(toIntegrity baseline))

                tryResolve<ILogger> services
                |> Option.iter (fun logger -> logger.Info(describe baseline))

                match enforce baseline with
                | Ok() -> Some baseline
                | Error refusal -> failwith (CompositionProfileRefusal.describe refusal)
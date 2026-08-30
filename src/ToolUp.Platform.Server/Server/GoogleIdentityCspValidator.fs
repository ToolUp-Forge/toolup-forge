// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.GoogleIdentityCspValidator

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Google Identity Services CSP preflight ──────────────────────────
//
// The `ToolUp.AuthProviders.GoogleIdentity.Client` companion loads
// Google's `gsi/client` library from `accounts.google.com`. Under an
// enforced Content-Security-Policy that origin is blocked unless
// `GoogleIdentityServicesCspContributor` was composed — and the
// failure is invisible from the server's side: the header is emitted,
// the app boots green, and the branded button silently never renders
// for anyone. The user sees an empty sign-in screen; the operator sees
// nothing at all.
//
// This validator makes the omission a startup signal. It follows the
// `DeployPlaneDepsValidator` shape — take the live `IServiceCollection`
// and probe what is actually registered — because that is the only
// place the answer exists: contributors are aggregated off the
// collection at compose time (`SecurityHardening.aggregate`), not held
// in `ServerConfig`.
//
// WHY IT MUST BE REGISTERED EXPLICITLY, and why that is not a gap: the
// GIS companion is CLIENT-tier. Nothing on the server can observe that
// a browser bundle composed it — `ClientConfig` never reaches this
// process. So the deployment that composes the companion registers
// this validator alongside it, and that registration IS the signal
// ("this deployment renders the Google button"). A deployment that
// does not use GIS never registers it and pays nothing (GP 13), which
// is exactly why the check cannot be auto-registered: it would fire on
// every redirect-flow Google deployment, none of which need the
// origins.
//
// Warning, never Error. A deployment may terminate its CSP at a proxy,
// or run report-only while it tunes a policy; refusing startup on an
// inference the server cannot fully make would turn a helpful signal
// into an outage. `NoSecurityHardening` short-circuits to `Ok` — no
// policy is emitted at all, so there is nothing to widen.

/// Host every GIS source shares. Matching on the host rather than on
/// exact source strings deliberately accepts a deployment that widened
/// the policy its own way (a bare `https://accounts.google.com`, a
/// hand-rolled contributor, a broader path): the question this
/// validator answers is "can the library load", not "did you use our
/// type".
[<Literal>]
let private googleAccountsHost = "accounts.google.com"

/// True when some registered contributor supplies BOTH a `script-src`
/// and a `frame-src` on Google's accounts host. Those two are the
/// load-bearing pair — without `script-src` the library never
/// downloads; without `frame-src` it downloads and then fails to
/// render, which is the more confusing of the two failures.
let private googleOriginsAllowed (services: IServiceCollection) =
    let contributed =
        services
        |> Seq.filter (fun d -> d.ServiceType = typeof<ICspContributor>)
        |> Seq.collect (fun d ->
            match d.ImplementationInstance with
            | :? ICspContributor as c -> c.RequiredSources
            // A factory / constructor-injected descriptor cannot be
            // introspected here any more than it can in
            // `SecurityHardening.aggregate`. That aggregator fails loudly
            // on one; this validator is a preflight and must not be the
            // thing that raises it, so the descriptor is skipped and the
            // aggregator keeps ownership of that error.
            | _ -> [])
        |> List.ofSeq

    let hasScript =
        contributed
        |> List.exists (function
            | ScriptSrc url -> url.Contains googleAccountsHost
            | _ -> false)

    let hasFrame =
        contributed
        |> List.exists (function
            | FrameSrc url -> url.Contains googleAccountsHost
            | _ -> false)

    hasScript && hasFrame

/// Warn when a deployment declares it renders the Google Identity
/// Services sign-in surface but has composed no CSP contributor for
/// Google's origins. Register alongside the client companion:
///
///   ServerApp.withConfigValidator (GoogleIdentityCspValidator(config, services) :> IConfigValidator)
///
/// `services` is the same `IServiceCollection` the composition root
/// holds, so the probe sees every contributor registered before
/// preflight runs.
type GoogleIdentityCspValidator(config: ServerConfig, services: IServiceCollection, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "google-identity-csp"
        member _.Timeout = timeout

        member _.Validate() = async {
            match config.SecurityHardening with
            | NoSecurityHardening -> return Ok
            | _ ->
                if googleOriginsAllowed services then
                    return Ok
                else
                    return
                        Warning(
                            "This deployment registered the Google Identity Services preflight (so it renders the Google branded button / One Tap), "
                            + "but no ICspContributor supplies both a script-src and a frame-src on accounts.google.com. "
                            + "Under the enforced Content-Security-Policy the GIS library will be blocked and the button will silently never render. "
                            + "Compose `ServerApp.withCspContributor (GoogleIdentityServicesCspContributor())`. "
                            + "If the policy is terminated at a proxy rather than by this app, or the deployment uses the redirect flow "
                            + "(OidcPresets.google) rather than the client companion, this warning does not apply."
                        )
        }
// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SecurityHardening

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform

// ─── Phase 9j — CSP aggregation / orchestration ──────────────────────
//
// Folds every registered `ICspContributor` plus a fixed `'self'`
// baseline into one `Content-Security-Policy` header value, computed
// once at compose time and handed to `CspMiddleware` as a DI
// singleton. `NoSecurityHardening` short-circuits to an empty header
// so the middleware no-ops and the default deployment is byte-for-byte
// unchanged (GP 13).
//
// The directive set is deliberately conservative and matches the
// SDK's reference client (Vite-bundled, same-origin scripts):
//   * `script-src 'self'` in BOTH modes — the bundle is same-origin;
//     no `'unsafe-inline'` for scripts at any hardening level.
//   * `style-src 'self' 'unsafe-inline'` under Default (Feliz inline
//     styles + Tailwind), tightened to `'self'` under Strict.
//   * Strict additionally adds `object-src 'none'` and
//     `upgrade-insecure-requests`.

/// The compose-time-resolved CSP header value. Registered as a DI
/// singleton; `CspMiddleware` resolves it. `Header = ""` means
/// "stamp nothing" (the `NoSecurityHardening` short-circuit, or a
/// hardening mode that produced no policy).
type ResolvedCspPolicy = { Header: string }

module ResolvedCspPolicy =
    let empty: ResolvedCspPolicy = { Header = "" }

/// Distinct, order-preserving append. CSP tolerates duplicate source
/// tokens but a deduped header is smaller and easier to audit.
let private dedupe (xs: string list) : string list =
    xs
    |> List.fold
        (fun (seen, acc) x ->
            if List.contains x seen then
                seen, acc
            else
                x :: seen, x :: acc)
        ([], [])
    |> snd
    |> List.rev

/// Build the `Content-Security-Policy` header value for `mode` given
/// the aggregated contributor directives. Pure — unit-testable without
/// a service collection.
let buildPolicy (mode: SecurityHardeningMode) (contributed: CspSourceDirective list) : string =
    match mode with
    | NoSecurityHardening -> ""
    | DefaultSecurityHardening
    | StrictSecurityHardening ->
        let strict = (mode = StrictSecurityHardening)

        let pick chooser =
            contributed |> List.choose chooser |> dedupe

        let connect =
            pick (function
                | ConnectSrc u -> Some u
                | _ -> None)

        let script =
            pick (function
                | ScriptSrc u -> Some u
                | _ -> None)

        let style =
            pick (function
                | StyleSrc u -> Some u
                | _ -> None)

        let img =
            pick (function
                | ImgSrc u -> Some u
                | _ -> None)

        let font =
            pick (function
                | FontSrc u -> Some u
                | _ -> None)

        let frame =
            pick (function
                | FrameSrc u -> Some u
                | _ -> None)

        let directive name (tokens: string list) = name + " " + String.concat " " tokens

        let styleBaseline =
            if strict then
                [ "'self'" ]
            else
                [ "'self'"; "'unsafe-inline'" ]

        let frameDirective =
            // No contributed frame origins → lock embedding down hard.
            if List.isEmpty frame then
                "frame-src 'none'"
            else
                directive "frame-src" ("'self'" :: frame)

        [
            "default-src 'self'"
            directive "script-src" ("'self'" :: script)
            directive "style-src" (styleBaseline @ style)
            directive "img-src" ([ "'self'"; "data:"; "blob:" ] @ img)
            directive "font-src" ([ "'self'"; "data:" ] @ font)
            directive "connect-src" ("'self'" :: connect)
            frameDirective
            "frame-ancestors 'none'"
            "form-action 'self'"
            "base-uri 'self'"
            if strict then
                "object-src 'none'"
            if strict then
                "upgrade-insecure-requests"
        ]
        |> String.concat "; "

/// Walk `services` for every `AddSingleton<ICspContributor>(instance)`
/// registration, collect their `RequiredSources`, and build the
/// resolved policy for `config.SecurityHardening`. Mirrors
/// `HealthCheckAggregator` / `ConfigValidatorAggregator`: instance
/// descriptors only — a factory/constructor-injected registration
/// cannot be inspected at compose time and fails loudly rather than
/// silently dropping a contributor's origins from the policy.
///
/// Must run near end-of-compose, AFTER every companion has had a
/// chance to call `services.AddSingleton<ICspContributor>(...)` (i.e.
/// after the `ComposeExtensions.ServiceConfig` hook) and BEFORE
/// `builder.Build()` seals the collection.
let aggregate (services: IServiceCollection) (config: ServerConfig) : ResolvedCspPolicy =
    match config.SecurityHardening with
    | NoSecurityHardening -> ResolvedCspPolicy.empty
    | mode ->
        let contributed =
            services
            |> Seq.filter (fun d -> d.ServiceType = typeof<ICspContributor>)
            |> Seq.collect (fun d ->
                match d.ImplementationInstance with
                | :? ICspContributor as c -> c.RequiredSources
                | _ ->
                    let implTypeName =
                        if isNull d.ImplementationType then
                            "<unknown>"
                        else
                            d.ImplementationType.FullName

                    failwithf
                        "ICspContributor must be registered as an instance via services.AddSingleton<ICspContributor>(instance). Descriptor for implementation type %s uses a factory or constructor-injected pattern the CSP aggregator cannot introspect at compose time. See the platform technical guide 'Phase 9j — security hardening'."
                        implTypeName)
            |> List.ofSeq

        {
            Header = buildPolicy mode contributed
        }
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

// ─── Phase 156 — per-request nonce / per-content hash CSP sources ─────
//
// The static aggregated CSP above cannot cover a deployment's own
// SSR-emitted inline `<script>` / `<style>` without weakening the policy
// with `'unsafe-inline'`. The two source modes browsers accept close
// that gap:
//   * **Nonce** — a per-request random nonce in `script-src` /
//     `style-src` `'nonce-…'`, surfaced to layouts so inline tags stamp
//     the matching value. For DYNAMIC responses; NOT cache-safe (a cached
//     body's fixed nonce mismatches a fresh per-request header nonce).
//   * **Hash** — `'sha256-…'` source hashes over a declared set of
//     inline-script bodies. Byte-stable header, so it survives HTML-body
//     caching + `304`s. For CACHED / DETERMINISTIC responses.
// Default `StaticCsp` → the resolved header is byte-for-byte pre-156 (GP
// 11); a deployment pays nothing for either mode until it opts in (GP 13).

/// The opaque sentinel `aggregate` bakes into the header template (inside
/// the `script-src` / `style-src` `'nonce-…'` tokens) in nonce mode;
/// `CspMiddleware` replaces it verbatim with a fresh per-request nonce.
/// Chosen to never collide with a real CSP source value.
[<Literal>]
let noncePlaceholder = "{TOOLUP_CSP_NONCE}"

/// The compose-time-resolved CSP header value. Registered as a DI
/// singleton; `CspMiddleware` resolves it. `Header = ""` means
/// "stamp nothing" (the `NoSecurityHardening` short-circuit, or a
/// hardening mode that produced no policy).
type ResolvedCspPolicy = {
    Header: string
    /// Phase 156 — `true` when `Header` carries the `noncePlaceholder`
    /// token (nonce source mode): `CspMiddleware` substitutes a fresh
    /// per-request nonce, stashes it for layouts (`Csp.requestNonce`), and
    /// skips the header on a `304` so a cached body's nonce stays
    /// authoritative. `false` → `Header` is byte-stable and stamped
    /// verbatim every request (static + hash source modes).
    NonceMode: bool
}

module ResolvedCspPolicy =
    let empty: ResolvedCspPolicy = { Header = ""; NonceMode = false }

/// Phase 156 — CSP source mode: how the aggregated policy covers a
/// deployment's own SSR-emitted inline `<script>` / `<style>` without
/// `'unsafe-inline'`. Default `StaticCsp` (pre-156 behaviour). Opt in
/// with `ServerApp.withCspSourceMode`, which registers the chosen mode as
/// the DI singleton `aggregate` reads. Absent registration → `StaticCsp`
/// → the resolved header is byte-for-byte pre-156 (GP 11).
type CspSourceMode =
    /// No nonce / hash sources — the pre-156 static aggregated header.
    | StaticCsp
    /// Per-request cryptographically-random nonce. `CspMiddleware`
    /// substitutes a fresh `'nonce-…'` into `script-src` / `style-src` on
    /// every request and surfaces it to layouts via `Csp.requestNonce`, so
    /// SSR inline `<script nonce="…">` / `<style nonce="…">` execute under
    /// a policy with no `'unsafe-inline'`. For DYNAMIC responses — NOT
    /// cache-safe: a cached body's fixed nonce mismatches a fresh
    /// per-request header nonce (the nonce↔cache validator warns when this
    /// mode is composed alongside a render cache).
    | NonceCsp
    /// `'sha256-…'` source hashes over a declared set of inline-`<script>`
    /// bodies, added to `script-src`. The header is byte-stable across
    /// requests, so it survives HTML-body caching + `304`s — the cache-safe
    /// counterpart to `NonceCsp`, for DETERMINISTIC / cached responses. The
    /// consumer hands the exact inline-script bodies it emits; the hash is
    /// computed over those bytes, so the emitted `<script>` must match
    /// byte-for-byte.
    | HashCsp of inlineScripts: string list

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

/// Build the `Content-Security-Policy` header value for `mode` given the
/// aggregated contributor directives, plus Phase 156 extra source tokens
/// folded into `script-src` (`extraScript`) and `style-src` (`extraStyle`)
/// — the `'nonce-…'` placeholder (nonce mode) or `'sha256-…'` hashes (hash
/// mode). Pure — unit-testable without a service collection. With both
/// extras empty the output is byte-for-byte the pre-156 static header.
let buildPolicyWith
    (mode: SecurityHardeningMode)
    (contributed: CspSourceDirective list)
    (extraScript: string list)
    (extraStyle: string list)
    : string =
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
            directive "script-src" (("'self'" :: script) @ extraScript)
            directive "style-src" (styleBaseline @ style @ extraStyle)
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

/// Build the static `Content-Security-Policy` header value for `mode`
/// given the aggregated contributor directives. The pre-156 entry point —
/// no nonce / hash sources. Pure — unit-testable without a service
/// collection.
let buildPolicy (mode: SecurityHardeningMode) (contributed: CspSourceDirective list) : string =
    buildPolicyWith mode contributed [] []

/// Phase 156 — sha256 of an inline-`<script>` body as a CSP `'sha256-…'`
/// source value (base64, no surrounding quotes). The CSP hash matches the
/// EXACT bytes between the `<script>` tags, so the consumer must hash the
/// same string it emits, byte-for-byte.
let inlineScriptHash (content: string) : string =
    let bytes =
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes content)

    System.Convert.ToBase64String bytes

/// Phase 156 — the consumer-registered `CspSourceMode` singleton (default
/// `StaticCsp`). Scanned the same instance-descriptor way as
/// `ICspContributor` so resolution is decoupled from the consumer's
/// composition order. A factory/constructor-injected registration is
/// ignored (it cannot be introspected at compose time) — the default
/// `StaticCsp` keeps the deployment byte-for-byte pre-156 rather than
/// failing, since an un-introspectable source mode is a no-harm fallback
/// (unlike a dropped contributor origin).
let resolveSourceMode (services: IServiceCollection) : CspSourceMode =
    services
    |> Seq.tryPick (fun d ->
        if d.ServiceType = typeof<CspSourceMode> then
            match d.ImplementationInstance with
            | :? CspSourceMode as m -> Some m
            | _ -> None
        else
            None)
    |> Option.defaultValue StaticCsp

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

        // Phase 156 — fold the opted-in source mode into the resolved
        // header. `StaticCsp` (the default) reproduces the pre-156 header
        // exactly; `NonceCsp` bakes the per-request nonce placeholder into
        // script-src/style-src (substituted by CspMiddleware); `HashCsp`
        // folds `'sha256-…'` source hashes over the declared inline scripts
        // into script-src (byte-stable, cache-safe).
        match resolveSourceMode services with
        | StaticCsp -> {
            Header = buildPolicy mode contributed
            NonceMode = false
          }
        | NonceCsp ->
            let nonceSource = sprintf "'nonce-%s'" noncePlaceholder

            {
                Header = buildPolicyWith mode contributed [ nonceSource ] [ nonceSource ]
                NonceMode = true
            }
        | HashCsp inlineScripts ->
            let hashSources =
                inlineScripts |> List.map (fun s -> sprintf "'sha256-%s'" (inlineScriptHash s))

            {
                Header = buildPolicyWith mode contributed hashSources []
                NonceMode = false
            }
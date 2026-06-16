// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 9j — Content-Security-Policy contributor extension ────────
//
// `ICspContributor` is the seam that makes the deployment's CSP
// "correct by construction": every SDK subsystem or companion that
// needs a non-`'self'` origin (the OIDC issuer, an AI provider host,
// a CDN-delivered grid bundle) declares exactly the directives it
// requires, and `SecurityHardening.aggregate` folds them into one
// header at compose time. No deployment hand-maintains a CSP string
// that drifts out of sync with the companions it actually runs.
//
// Companion contract (mirrors `IHealthCheck` / `IConfigValidator`):
// register the instance via `services.AddSingleton<ICspContributor>(c)`
// — through `ServerApp.withCspContributor` or the companion's
// `ComposeExtensions.ServiceConfig` hook. `SecurityHardening.aggregate`
// walks the `IServiceCollection` for every such registration at
// compose time. Constructor-injected / factory descriptors are NOT
// inspected (same limitation, and same loud-failure rationale, as the
// health-check and config-validator aggregators).
//
// Portability (GP 12): the contract returns plain values (a DU list),
// is async-free (compose-time, not request-time), carries no
// framework types, is DI-registered, and is contract-tested in
// `ToolUp.Platform.Tests`. Lives in `ToolUp.Platform.Server` because
// CSP is an HTTP-response concern with no client/Fable surface — it
// is consumed only by server-side companions.

/// One Content-Security-Policy source a contributor needs added to a
/// specific directive. The aggregator always seeds `'self'` for every
/// directive; contributors only ever *widen* the policy, never
/// replace the baseline.
type CspSourceDirective =
    /// Added to `connect-src` — XHR / fetch / EventSource / WebSocket
    /// targets (OIDC issuer, AI provider API host, telemetry sink).
    | ConnectSrc of url: string
    /// Added to `script-src` — executable script origins
    /// (CDN-delivered library bundle).
    | ScriptSrc of url: string
    /// Added to `style-src` — stylesheet origins.
    | StyleSrc of url: string
    /// Added to `img-src` — image origins beyond `'self' data: blob:`.
    | ImgSrc of url: string
    /// Added to `font-src` — webfont origins beyond `'self' data:`.
    | FontSrc of url: string
    /// Added to `frame-src` — embeddable child-frame origins
    /// (hosted checkout, embedded auth widget).
    | FrameSrc of url: string
    /// Added to `worker-src` — Worker / SharedWorker / ServiceWorker
    /// script origins (a client library that spawns Web Workers from a
    /// `blob:` URL, e.g. `<model-viewer>`). When NO contributor supplies
    /// a `worker-src`, the aggregator emits no `worker-src` directive at
    /// all and workers fall back to `default-src 'self'` — the baseline
    /// policy stays byte-for-byte unchanged (GP 11). When any contributor
    /// supplies one, the aggregator promotes `worker-src` off the fallback
    /// with `'self'` seeded ahead of the contributed source(s).
    | WorkerSrc of source: string

/// A subsystem or companion that needs the deployment CSP widened.
/// Registered as a DI singleton; aggregated at compose time. The
/// implementation is invoked exactly once during composition, so it
/// may read process configuration (env vars) but must not depend on
/// request state.
type ICspContributor =
    /// The directives this contributor needs added to the aggregated
    /// policy. Return `[]` to contribute nothing (e.g. an env-gated
    /// contributor whose activating variable is unset) — registering
    /// an inert contributor is cheaper than conditional registration
    /// at the call site.
    abstract member RequiredSources: CspSourceDirective list

// ─── First-party default contributors ────────────────────────────────

/// OIDC issuer → `connect-src`. The browser-side auth client performs
/// discovery / token / JWKS fetches against the issuer host; without
/// it in `connect-src` an enforced CSP breaks sign-in. Reads
/// `TOOLUP_OIDC_ISSUER` (the same env var `OidcConfigCompletenessValidator`
/// keys on) rather than the OIDC companion's config type — keeping the
/// SDK↔companion boundary clean. Inert when the variable is unset
/// (HeaderAuthProvider dev deployments contribute nothing).
///
/// Contributes the issuer's **origin only** (scheme + host + port) rather
/// than the raw issuer URL. CSP source matching is exact-match on path
/// unless the source path ends in `/`, so a raw issuer like
/// `https://login.microsoftonline.com/{tenant}/v2.0` (no trailing slash)
/// matches only that exact path — blocking discovery at
/// `{issuer}/.well-known/openid-configuration` and the token endpoint
/// at `/{tenant}/oauth2/v2.0/token` (different path prefix from the
/// `/v2.0` issuer). Stripping to the origin matches every path on the
/// issuer host, which is what OIDC sign-in actually needs.
///
/// Falls back to the raw issuer (pre-Phase 3b.C behaviour) if the
/// value is not a parseable URI — keeps the contributor inert-safe.
type OidcIssuerCspContributor() =
    static member val EnvVar = "TOOLUP_OIDC_ISSUER" with get

    interface ICspContributor with
        member _.RequiredSources =
            match Environment.GetEnvironmentVariable OidcIssuerCspContributor.EnvVar with
            | null
            | "" -> []
            | issuer ->
                let trimmed = issuer.Trim()

                let connectSrc =
                    try
                        let uri = System.Uri(trimmed)

                        if uri.IsDefaultPort then
                            sprintf "%s://%s" uri.Scheme uri.Host
                        else
                            sprintf "%s://%s:%d" uri.Scheme uri.Host uri.Port
                    with _ ->
                        trimmed

                [ ConnectSrc connectSrc ]

/// Built-in AI provider API hosts → `connect-src`. The
/// `ToolUp.AI`/`AIProviders` companions stream completions directly
/// from the browser-adjacent server, but BYOK and any future
/// browser-initiated provider call need these origins reachable. Safe
/// widening: `connect-src` only, no script/style surface. A deployment
/// using neither provider carries two unused `connect-src` tokens —
/// harmless and cheaper than companion-introspection.
type AiProviderCspContributor() =
    interface ICspContributor with
        member _.RequiredSources = [ ConnectSrc "https://api.anthropic.com"; ConnectSrc "https://api.openai.com" ]

/// AG Grid / AG Charts Enterprise jsDelivr CDN → `script-src` /
/// `style-src`. Only relevant to deployments that load the Enterprise
/// bundle from the CDN rather than the Vite-bundled npm package (the
/// default in this stack bundles, so this contributor is opt-in via
/// `ServerApp.withCspContributor` — it is NOT auto-registered). Kept
/// here so CDN-delivered deployments have a first-party, reviewed
/// contributor instead of hand-rolling the host.
type AgGridCdnCspContributor() =
    interface ICspContributor with
        member _.RequiredSources = [ ScriptSrc "https://cdn.jsdelivr.net"; StyleSrc "https://cdn.jsdelivr.net" ]

/// `blob:` Web Workers → `worker-src`. Client libraries that spawn
/// Workers from a `blob:` URL (Google's `<model-viewer>` web component
/// is the canonical case) are blocked under the hardened policy because
/// the SDK baseline emits no `worker-src` directive, so workers fall back
/// to `default-src 'self'`. A deployment composing such a companion
/// registers this contributor — `ServerApp.withCspContributor
/// (BlobWorkerCspContributor())` — to widen the policy to
/// `worker-src 'self' blob:`. Opt-in (NOT auto-registered): a deployment
/// that runs no blob-worker library contributes nothing and the baseline
/// stays unchanged (GP 11). `img-src` already carries `blob:` in the SDK
/// baseline, so no `ImgSrc` widening is needed here.
type BlobWorkerCspContributor() =
    interface ICspContributor with
        member _.RequiredSources = [ WorkerSrc "blob:" ]
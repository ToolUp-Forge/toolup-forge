module ToolUp.Platform.SecurityHeadersValidator

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 6l.K / 129d — thin SecurityHeaders on internet-facing auth ─
//
// Phase 129d gave `SecurityHeadersMiddleware` an always-on baseline floor
// (`X-Frame-Options: DENY` / `nosniff` / `Referrer-Policy`, plus HSTS when
// `RequireHttps`), so a stock deployment is no longer framable/sniffable
// by default. This validator's remaining job is to flag the gap the floor
// deliberately does NOT close: an internet-facing authenticated deployment
// with neither a `Content-Security-Policy` (the floor ships none — a
// default CSP breaks real apps) nor the richer `productionDefaults`
// posture (preload HSTS + `includeSubDomains` + a baseline CSP). The
// remaining gap exists only when `SecurityHeaders = Map.empty` AND
// `SecurityHardening = NoSecurityHardening` (otherwise `CspMiddleware`
// stamps an aggregated CSP, so no warning is warranted).
//
// `Warning` (not `Error`): (1) some deployments serve only API responses
// (no browser surface) and don't need a CSP; (2) SDK consumers might emit
// a CSP via a custom middleware ahead of this one. The warning surfaces in
// HealthMonitorUI / `/dev/inspect` so the operator can set
// `ServerConfig.SecurityHeaders = SecurityHeaders.productionDefaults`,
// call `withSecurityHardening`, or knowingly ignore.
//
// Heuristic for "internet-facing": `Mode != Anonymous` AND
// (`RequireHttps = true` OR `TrustForwardedHeaders = true`). Mirrors
// `RateLimitModeValidator` — the TLS-terminated-at-LB topology keeps
// `RequireHttps = false` but sets `TrustForwardedHeaders = true`, so
// keying only on `RequireHttps` missed the most common internet-facing
// deployment shape.

/// Phase 6l.K / 129d — config validator that warns when an
/// internet-facing authenticated deployment ships no CSP: empty
/// `SecurityHeaders` AND `NoSecurityHardening` (the 129d floor covers the
/// other baseline headers, so this flags only the remaining CSP gap).
type SecurityHeadersValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "security-headers-mode"
        member _.Timeout = timeout

        member _.Validate() = async {
            let requiresAuth = DeploymentConfig.requiresAnyAuth config
            let internetFacing = DeploymentConfig.isInternetFacing config
            let headersEmpty = config.SecurityHeaders.IsEmpty
            let noHardening = config.SecurityHardening = NoSecurityHardening

            if requiresAuth && internetFacing && headersEmpty && noHardening then
                return
                    Warning(
                        sprintf
                            "ServerConfig.Surfaces = %s + (RequireHttps or TrustForwardedHeaders) + SecurityHeaders = Map.empty + SecurityHardening = NoSecurityHardening. Phase 129d auto-stamps a baseline (X-Frame-Options DENY / nosniff / Referrer-Policy strict-origin-when-cross-origin, plus HSTS when RequireHttps), but this deployment still ships no Content-Security-Policy (XSS surface widened) and no preload/includeSubDomains HSTS posture. Set ServerConfig.SecurityHeaders = SecurityHeaders.productionDefaults for the SDK's recommended baseline (preload HSTS / nosniff / X-Frame-Options DENY / Referrer-Policy / baseline CSP), merge productionDefaults with your own per-deployment overrides, or call withSecurityHardening for an auto-generated companion-aware CSP. API-only deployments that legitimately don't need browser-targeted headers can ignore this warning."
                            (DeploymentConfig.surfacesLabel config)
                    )
            else
                return Ok
        }

// ─── Phase 156 — nonce CSP source mode ↔ render-cache hazard ──────────
//
// Nonce mode (`SecurityHardening.CspSourceMode = NonceCsp`) bakes a
// per-request nonce into both the CSP header and the response body's
// inline `<script nonce="…">` tags. A render cache (or a `304`) serves a
// STORED body whose nonce is fixed at render time, while `CspMiddleware`
// would emit a fresh per-request nonce in the header — the two mismatch
// and the browser blocks the inline script on every cache hit. This is a
// silent break (no error, just a dead inline script), so it warrants a
// startup warning steering the operator to hash mode for cacheable
// deployments (whose header is byte-stable and survives caching + `304`s).
//
// Detection is by scanning the composed `IServiceCollection`: the source
// mode is the registered `SecurityHardening.CspSourceMode` singleton, and
// the render cache is any registered service whose type is
// `ToolUp.PublicRendering.IRenderCache`. The latter is matched by full
// type name rather than a typed reference because `ToolUp.Platform.Server`
// does not (and must not) depend on `ToolUp.PublicRendering` — the cache
// is an upper-layer companion. The scan runs at `Validate()` time (config
// preflight, near end-of-compose), by which point every companion's
// `ServiceConfig` hook — including the render-cache registration — has run.

/// The full type name of the PublicRendering render cache. Matched by name
/// to avoid a layering dependency from `ToolUp.Platform.Server` onto the
/// upper-layer `ToolUp.PublicRendering` companion.
[<Literal>]
let private renderCacheTypeName = "ToolUp.PublicRendering.IRenderCache"

/// Phase 156 — warns when `CspSourceMode = NonceCsp` is composed alongside
/// a registered `IRenderCache`: a cached body's fixed nonce mismatches the
/// fresh per-request header nonce, silently breaking the inline script on
/// every cache hit. Steers the operator to hash mode. `Ok` in every other
/// configuration — static / hash mode, or nonce mode with no cache.
type CspNonceCacheValidator(services: IServiceCollection, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    let nonceModeSelected () =
        SecurityHardening.resolveSourceMode services = SecurityHardening.NonceCsp

    let renderCacheRegistered () =
        services
        |> Seq.exists (fun d -> not (isNull d.ServiceType) && d.ServiceType.FullName = renderCacheTypeName)

    interface IConfigValidator with
        member _.Name = "csp-nonce-render-cache"
        member _.Timeout = timeout

        member _.Validate() = async {
            if nonceModeSelected () && renderCacheRegistered () then
                return
                    Warning(
                        "SecurityHardening.CspSourceMode = NonceCsp is composed alongside a registered IRenderCache (SSR render cache). A per-request CSP nonce is baked into both the response header and the body's inline <script nonce=…> tags; when a body is served from the render cache (or returned as a 304), its stored nonce is fixed while CspMiddleware emits a fresh per-request nonce — the two mismatch and the browser blocks the inline script on every cache hit. Use SecurityHardening.HashCsp (sha256 source hashes over your declared inline-script bodies) for cacheable responses: the CSP header is byte-stable across requests and survives caching + 304s. See docs/migrations/156-csp-nonce-hash.md for the dynamic→nonce / cached→hash decision matrix."
                    )
            else
                return Ok
        }
module ToolUp.Platform.SecurityHeaders

// ─── SDK production defaults for ServerConfig.SecurityHeaders ───────
//
// `ServerConfig.SecurityHeaders = Map.empty` is the default — the
// `SecurityHeadersMiddleware` skips work entirely, no headers stamped.
// Correct for local dev. Wrong for any internet-facing deployment:
//   - No HSTS → first-visit downgrade attacks unprotected
//   - No CSP → broader XSS attack surface
//   - No X-Frame-Options → clickjacking via embedded iframe
//   - No nosniff → some MIME-confusion vectors stay open
//
// Operators wanting the SDK's recommended baseline assign this map
// to `ServerConfig.SecurityHeaders` (or via a future
// ServerApp.withSecurityHeaders helper). Per-route overrides
// continue to work — the middleware skips keys already present on
// the response, so a handler that writes its own CSP wins.

/// The SDK's recommended baseline security headers for production
/// deployments. Operators picking this up via
/// `ServerConfig.SecurityHeaders = SecurityHeaders.productionDefaults`
/// get HSTS + clickjacking + sniff + Referrer + a baseline CSP. A
/// deployment with stricter requirements (per-source CSP, embedded
/// content allowances) merges this with its own overrides.
///
/// HSTS is set to one year + includeSubDomains + preload — the
/// recommended posture for deployments owning their domain. If the
/// deployment shares a parent domain with non-HSTS subdomains, drop
/// `includeSubDomains` before applying.
let productionDefaults: Map<string, string> =
    Map.ofList [
        "Strict-Transport-Security", "max-age=31536000; includeSubDomains; preload"
        "X-Content-Type-Options", "nosniff"
        "X-Frame-Options", "DENY"
        "Referrer-Policy", "strict-origin-when-cross-origin"
        // Baseline CSP — same-origin scripts/styles/connect, no inline
        // by default. Deployments using inline styles (Tailwind config,
        // Feliz dynamic styles) typically need to widen this; the SDK's
        // reference client (Vite-bundled) operates within these limits.
        "Content-Security-Policy",
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; \
         img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; \
         frame-ancestors 'none'; form-action 'self'; base-uri 'self'"
    ]

// ─── Phase 129d — always-on baseline floor ─────────────────────────
//
// `SecurityHeadersMiddleware` stamps this floor on every response of
// every deployment, beneath the consumer's `ServerConfig.SecurityHeaders`
// map and beneath per-route handler-set headers (both still win — the
// middleware skips a key that is already present). The point is that a
// stock deployment is no longer framable / sniffable by default: it
// carries `X-Frame-Options` / `nosniff` / `Referrer-Policy` without any
// operator action (GP 9 — no silent insecure default), while a consumer
// that already set any of these is byte-for-byte unchanged (GP 11 —
// additive; consumer-set headers win).
//
// Deliberately NO `Content-Security-Policy` here: a default CSP breaks
// real apps (inline styles, third-party widgets), so CSP stays the
// opt-in `SecurityHardening` path (companion-aware `CspMiddleware`) plus
// the richer `productionDefaults` map above. The floor is only the three
// headers that are safe for every deployment shape, plus HSTS gated on
// the bind actually enforcing HTTPS.

/// The three always-safe headers stamped on every response (Phase 129d).
/// No CSP (that stays the `SecurityHardening` / `productionDefaults`
/// opt-in) and no HSTS (added by `effective` only when the bind enforces
/// HTTPS — sending HSTS over plain HTTP would be a no-op at best and a
/// foot-gun for local dev at worst).
let baseline: Map<string, string> =
    Map.ofList [
        "X-Frame-Options", "DENY"
        "X-Content-Type-Options", "nosniff"
        "Referrer-Policy", "strict-origin-when-cross-origin"
    ]

/// HSTS value added to the floor when `ServerConfig.RequireHttps` is set
/// (the bind enforces HTTPS at this layer — `isHttpsTerminatedHere`). One
/// year + `includeSubDomains`; no `preload` (a domain-owner commitment
/// the SDK can't assume — `productionDefaults` opts into that). A
/// deployment with non-HSTS subdomains overrides this via the
/// `SecurityHeaders` map.
[<Literal>]
let hstsHeaderValue = "max-age=31536000; includeSubDomains"

/// Phase 129d — resolve the effective header set the middleware stamps:
/// the always-on `baseline` floor (plus HSTS when `requireHttps`) with
/// the consumer's configured map layered ON TOP, so a consumer-set value
/// for any baseline key wins. Per-route handler overrides win over both —
/// that is enforced in the middleware (skip-if-present), not here. Pure
/// + total so it is directly unit-testable.
let effective (requireHttps: bool) (configured: Map<string, string>) : Map<string, string> =
    let floor =
        if requireHttps then
            baseline |> Map.add "Strict-Transport-Security" hstsHeaderValue
        else
            baseline

    // Consumer map wins on key collision (floor is the lowest priority).
    configured |> Map.fold (fun acc k v -> Map.add k v acc) floor
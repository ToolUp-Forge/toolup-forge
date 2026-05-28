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
// `ServerApp.withSecurityHeaders` helper). Per-route overrides
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
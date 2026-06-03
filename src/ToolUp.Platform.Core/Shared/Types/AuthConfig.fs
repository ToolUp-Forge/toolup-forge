// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// JWS signature algorithms an OIDC-style auth provider can be configured
/// to accept. Identity-by-value (rule 1): the DU carries no live handles
/// and is safe to share across the framework-portable interfaces and the
/// JWT-parsing path. `tryParse` round-trips the canonical RFC 7518 string
/// representation a JWT header's `alg` field uses; unknown strings return
/// `None` and the validation pipeline maps them to `UnsupportedAlgorithm`.
///
/// `HS256` is deliberately absent — symmetric flows belong to
/// `StaticJwtAuthProvider`, whose secret never leaves the deployment;
/// an OIDC issuer publishing HS256 over JWKS would imply secret-sharing
/// with browser-side parties, which the OIDC trust model forbids. EdDSA /
/// Ed25519 are likewise omitted until customer demand surfaces.
type JwsAlgorithm =
    /// RSA-SHA256 with PKCS#1 v1.5 padding. The OIDC ecosystem's
    /// historical default; Entra / Azure AD / Auth0 / Okta all sign
    /// with RS256 unless explicitly reconfigured.
    | RS256
    /// RSA-SHA384 with PKCS#1 v1.5 padding. Same RSA key shape as
    /// RS256; differs only in hash strength.
    | RS384
    /// RSA-SHA512 with PKCS#1 v1.5 padding. Same RSA key shape as
    /// RS256 / RS384; rarely issued in practice but cheap to support
    /// alongside the others.
    | RS512
    /// ECDSA over the P-256 curve with SHA-256 hashing. AWS Cognito,
    /// Firebase Auth, and some Okta / dynamic-client OIDC flows issue
    /// ES256-signed tokens by default. JWS signature transport is the
    /// IEEE-P1363 fixed-field concatenation (r||s, 64 bytes for P-256),
    /// not DER-encoded.
    | ES256
    /// RSA-SHA256 with PSS (probabilistic) padding. Modern-RSA
    /// alternative to RS256 with the same key shape; some hardened
    /// IdPs default to PS256 to avoid PKCS#1 padding attacks against
    /// historically weak implementations.
    | PS256

module JwsAlgorithm =
    /// Render the canonical RFC 7518 `alg` string. Used in error
    /// messages (`UnsupportedAlgorithm`) and in the per-algorithm
    /// docs / migration tables.
    let toString =
        function
        | RS256 -> "RS256"
        | RS384 -> "RS384"
        | RS512 -> "RS512"
        | ES256 -> "ES256"
        | PS256 -> "PS256"

    /// Parse a JWT header `alg` value. Case-sensitive — RFC 7515
    /// requires the registered name verbatim. Unknown strings return
    /// `None` so the caller can decide whether to fall through to
    /// `UnsupportedAlgorithm` or some other error path.
    let tryParse (raw: string) : JwsAlgorithm option =
        match raw with
        | "RS256" -> Some RS256
        | "RS384" -> Some RS384
        | "RS512" -> Some RS512
        | "ES256" -> Some ES256
        | "PS256" -> Some PS256
        | _ -> None

/// How an `IAuthProvider` obtains the signing key used to verify JWT
/// signatures. Each provider consumes a subset — `StaticJwtAuthProvider`
/// uses `StaticSecret`; OIDC providers use `JwksDiscovery` (SaaS IdPs)
/// or `JwksExplicit` (self-hosted IdPs on non-standard paths).
type KeySource =
    /// HS256 shared secret, used by the token issuer and verifier.
    /// Appropriate for internal services that mint their own tokens;
    /// not suitable for browser-facing flows where the key would leak.
    | StaticSecret of key: string
    /// OIDC metadata discovery — fetch
    /// `{issuerUrl}/.well-known/openid-configuration`, read the
    /// `jwks_uri` field, then fetch the signing keys from there. The
    /// standard path for hosted OIDC providers (Clerk, Auth0,
    /// Azure AD, Okta, Google Identity).
    | JwksDiscovery of issuerUrl: string
    /// Direct JWKS URL. Skips the OIDC metadata lookup — useful when
    /// the JWKS is exposed on a non-standard path or a provider
    /// doesn't publish the metadata document.
    | JwksExplicit of jwksUrl: string

/// Where in the HTTP request a JWT will be found. `BearerHeader` is
/// the standard OIDC transport; `Cookie` and `CustomHeader` support
/// apps that mint tokens outside the OIDC flow or need HttpOnly
/// cookies for browser-side protection.
type TokenLocation =
    /// `Authorization: Bearer <token>`. Default for OIDC flows.
    | BearerHeader
    /// Token value is the named cookie's value. Used for HttpOnly
    /// browser sessions that never expose the token to JavaScript.
    | Cookie of name: string
    /// Token value is the named header's value. Used for non-standard
    /// protocols that expose tokens outside the bearer convention.
    | CustomHeader of name: string
    /// 0.5.3 — Check `Authorization: Bearer <token>` first, then fall
    /// back to the named cookie when the header is absent. Required for
    /// deployments that serve BOTH the standard REST auth path
    /// (Authorization-header-bearing XHR/fetch) AND SSE (EventSource,
    /// which the browser will only ever auth via cookie — the
    /// EventSource spec forbids custom request headers). The single-
    /// location cases above force a deployment to pick one transport
    /// or the other and silently break the unpicked path; this case
    /// admits both. Pairs with `ServerConfig.SseAuthMode = CookieRequired`
    /// and `UserSession.setAuthToken` on the client side, which already
    /// writes the JWT into both `localStorage` (for the Authorization
    /// header) and the matching cookie (for SSE).
    | BearerOrCookie of cookieName: string

/// Declarative configuration for an `IAuthProvider`. Providers read
/// the fields they care about and ignore the rest — e.g.
/// `StaticJwtAuthProvider` expects `KeySource = StaticSecret _`;
/// OIDC providers expect `JwksDiscovery` or `JwksExplicit`.
///
/// `Issuer` and `Audience` are claim-validation inputs when `Some`.
/// Leaving either `None` skips the corresponding check — acceptable
/// during development but SHOULD NOT be left unset in production OIDC
/// deployments where issuer and audience restrict the token's
/// intended recipient.
type AuthConfig = {
    /// Expected `iss` claim on inbound tokens; `None` disables the
    /// check. SaaS IdPs always populate `iss` with the issuer URL.
    Issuer: string option
    /// Expected `aud` claim on inbound tokens; `None` disables the
    /// check. Set per-tenant when the IdP issues audience-restricted
    /// tokens (i.e. almost always, in production).
    Audience: string option
    /// Where the verifier finds the signing key for inbound tokens.
    KeySource: KeySource
    /// Where the verifier extracts the JWT from the HTTP request.
    TokenLocation: TokenLocation
    /// Clock-skew tolerance (seconds) applied to `exp` and `nbf`
    /// claim validation. `None` -> SDK default (60s — standard
    /// practice, covers small NTP drift between deployment host and
    /// IdP). Cross-region setups with tighter / looser drift can
    /// override; raise cautiously since wider tolerance widens the
    /// replay window for stolen tokens.
    ClockSkewSeconds: int64 option
    /// JWS signature algorithms the deployment is willing to trust.
    /// `None` -> SDK default `[RS256]`, preserving today's behaviour
    /// for every existing consumer. Operators wanting to interoperate
    /// with IdPs that issue ES256 / RS384 / RS512 / PS256 (AWS
    /// Cognito, Firebase Auth, some Okta / dynamic-client OIDC flows,
    /// hardened RSA deployments) opt in explicitly via
    /// `Some [ RS256; ES256 ]` or similar. An inbound token whose
    /// header `alg` is not in this list is rejected with
    /// `UnsupportedAlgorithm` even if its signature would otherwise
    /// verify — the trust set is operator-owned, not auto-widened by
    /// the SDK. `HS256` is intentionally not a member: symmetric
    /// flows belong to `StaticJwtAuthProvider`.
    AcceptedAlgorithms: JwsAlgorithm list option
    /// 0.5.4 — When `Some true`, the OIDC provider's `userFromPayload`
    /// prefers the JWT `oid` claim over `sub` when constructing
    /// `AuthenticatedUser.UserId`. `oid` is Microsoft Entra's tenant-
    /// wide stable user identifier; `sub` is a pairwise pseudonymous
    /// identifier specific to the relying party (different per app
    /// registration). Bootstrapping platform admins, RBAC entries,
    /// and audit identity by the tenant-stable `oid` is operationally
    /// correct for Entra deployments — the operator looks up the
    /// admin's `oid` in the Entra portal once and the value stays
    /// stable across app registrations / token refreshes.
    ///
    /// `None` or `Some false` (default) preserves the provider's pre-
    /// 0.5.4 behaviour (`sub` only) — the SDK never silently switches
    /// identity sources for an existing deployment (GP 11).
    ///
    /// `AuthProvider.fromEnv` auto-enables this flag when the
    /// configured `Issuer` matches `https://login.microsoftonline.com/`
    /// — any other issuer leaves the flag `None`. Consumers wiring
    /// `AuthConfig` directly can set it explicitly.
    ///
    /// When the flag is on but the inbound token has no `oid` claim,
    /// the provider falls back to `sub` — never breaks the request
    /// path on a missing optional claim.
    PreferOidWhenPresent: bool option
}
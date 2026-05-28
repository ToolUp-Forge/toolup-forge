// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Canonical auth-pipeline metric names ────────────────────────────
//
// `IMetricsSink` counter names emitted by the OIDC + Entra External ID
// auth providers + the OIDC client-side refresh loop. Centralised so
// the names are stable across providers + observability sinks (Datadog,
// Prometheus, OpenTelemetry) can dashboard them without per-provider
// knowledge. Names follow the SDK-wide convention used by
// `toolup.ratelimit.*`, `toolup.oauth.token.*`, etc. — lower-case
// dot-separated, `_total` suffix on cumulative counters.
//
// Per-counter tag conventions: `provider` (always — distinguishes
// `oidc` from `entra-external-id` from any future wrapper); other tags
// optional per emission site. The provider tag matches the
// IAuthProvider implementation's logical identity, not the issuer URL
// (which is high-cardinality and would explode metric storage).

module AuthMetrics =
    /// JWT validated successfully; an authenticated user was resolved.
    [<Literal>]
    let ValidateSuccess = "toolup.auth.validate.success_total"

    /// No token was present on the request. Not a failure for the
    /// lenient `GetUser` path (Anonymous mode is normal); incremented
    /// alongside it so the counter reflects the actual rate of
    /// token-less requests.
    [<Literal>]
    let ValidateNoToken = "toolup.auth.validate.no_token_total"

    /// Signature verification failed — token was tampered with, the
    /// JWKS rotated and the key id is unknown, or the algorithm is
    /// not RS256.
    [<Literal>]
    let ValidateInvalidSignature = "toolup.auth.validate.invalid_signature_total"

    /// `exp` claim is in the past (beyond the clock-skew tolerance).
    [<Literal>]
    let ValidateExpired = "toolup.auth.validate.expired_total"

    /// `iss` claim does not match the configured `AuthConfig.Issuer`.
    [<Literal>]
    let ValidateInvalidIssuer = "toolup.auth.validate.invalid_issuer_total"

    /// `aud` claim does not contain the configured
    /// `AuthConfig.Audience` — token was issued for a different app.
    [<Literal>]
    let ValidateInvalidAudience = "toolup.auth.validate.invalid_audience_total"

    /// JWT header carries a `kid` the JWKS doesn't currently know
    /// about, even after a forced refresh. Usually means the IdP
    /// rotated keys and the new key hasn't published yet; transient
    /// in healthy deployments, persistent indicates IdP drift.
    [<Literal>]
    let ValidateUnknownKid = "toolup.auth.validate.unknown_kid_total"

    /// Token payload was malformed — not three segments, not valid
    /// base64-url, not parseable JSON. Indicates a misconfigured
    /// client or a non-JWT being mis-routed to the auth pipeline.
    [<Literal>]
    let ValidateMalformed = "toolup.auth.validate.malformed_total"

    /// Entra-specific claim post-processing failed silently (the
    /// inner OIDC provider accepted the token but the Entra wrapper
    /// could not re-parse the payload to read `oid` / `tid` / `idp`).
    /// Acceptable — Entra mappings are best-effort enrichment — but
    /// dashboard-worthy.
    [<Literal>]
    let EntraClaimParseFailed = "toolup.auth.entra.claim_parse_failed_total"

    /// Tag key for the provider identity (`oidc` / `entra-external-id`
    /// / etc.). Use as `Map.ofList [ ProviderTag, "oidc" ]` when
    /// constructing the per-emission tag map.
    [<Literal>]
    let ProviderTag = "provider"
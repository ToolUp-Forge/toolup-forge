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

    // `EntraClaimParseFailed` ("toolup.auth.entra.claim_parse_failed_total")
    // was removed at Phase 749 with the Entra External ID companion that
    // was its only emitter. The generic claim-mapping seam that replaced
    // that companion's post-processing is FAIL-CLOSED, so a payload it
    // cannot re-read is a rejected request counted under
    // `ValidateMalformed` above — an outcome, not the best-effort
    // enrichment failure this counter existed to make visible. A
    // dashboard carrying the old series will simply stop receiving
    // points; see `docs/migrations/0.23.0-entra-external-id-removal.md`.

    /// Tag key for the provider identity (`oidc` / `google` / etc.).
    /// Use as `Map.ofList [ ProviderTag, "oidc" ]` when constructing
    /// the per-emission tag map.
    [<Literal>]
    let ProviderTag = "provider"
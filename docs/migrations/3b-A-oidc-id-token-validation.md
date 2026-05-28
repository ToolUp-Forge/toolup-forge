# Phase 3b.A — Client-side `id_token` validation (consumer migration)

**What changes.** `OidcClient` (the browser-side OIDC sign-in companion) gains an opt-in validation pipeline for the `id_token` returned at the OAuth callback. With `OidcUIConfig.ValidateIdToken = Some true`, the callback handler verifies the id_token's RS256 signature against the issuer's JWKS via WebCrypto, then checks `iss`, `aud`, and `exp` (60s clock-skew tolerance) before persisting the access token locally. Cluster B1 already closed the nonce-binding gap; this phase carries the same hardening to the remaining id_token claims that today the browser trusts implicitly because the server validates the access_token on the next request.

**Scope.** Client-side only — no server-side change. The server's `OidcAuthProvider` already does the comprehensive validation on every protected request; Phase 3b.A is defence-in-depth that catches a forged or stale id_token at the callback boundary rather than holding it in browser state until the next 401.

**Default behaviour is unchanged.** `OidcUIConfig.ValidateIdToken` defaults to `None`, which resolves to "off" — every existing deployment is byte-for-byte today's behaviour. Operators opt in by setting the field to `Some true`. The default flips to `true` in a coordinated minor bump once consumers have adopted.

## What was added

- `OidcUIConfig.ValidateIdToken: bool option` — opt-in toggle for the validation pipeline. `None` and `Some false` are equivalent (validation skipped); `Some true` activates the pipeline at the callback boundary.
- `OidcIdTokenValidator` module — pure-F# JWT parser + `iss` / `aud` / `exp` validators with a 60s clock-skew default (mirrors the server-side `OidcAuthProvider.defaultClockSkewSeconds`).
- `OidcDiscovery.JwksUri` — discovery doc now lifts `jwks_uri` for client-side use.
- `OidcDiscovery.fetchJwks` — JWKS fetch with `sessionStorage` cache (10-minute TTL — mirrors the server-side cache's TTL; short enough to follow rotation, long enough that a multi-page session doesn't refetch on every navigation).
- `OidcClient.validateIdToken` — production entry point wiring WebCrypto's `crypto.subtle.verify` + the JWKS fetcher + `Date.now()`.
- `OidcClient.validateIdTokenWith` — runtime-injectable orchestrator (signature verifier + JWKS resolver + clock all parameters) so contract tests can exercise the pipeline in .NET.
- Five new `AuthError` cases — `MalformedIdToken`, `IdTokenSignatureInvalid`, `IdTokenIssuerInvalid`, `IdTokenAudienceInvalid`, `IdTokenExpired` — each rendered via `describeError` with a user-friendly fallback.

## When to opt in

Reach for `ValidateIdToken = Some true` on:

- **Any deployment with strong defence-in-depth requirements** — financial, healthcare, or any audit-sensitive context where catching a forged id_token at the callback (before any "signed-in" state persists) is materially better than catching it on the next 401.
- **Multi-tenant deployments where the same browser can sign in to different tenants** — the audience check ensures a token issued for tenant A can't be replayed against the page configured for tenant B.
- **Any deployment where the issuer is reachable from the browser** — WebCrypto-based verification requires the browser to fetch the JWKS, which means the issuer's JWKS endpoint must be reachable from the operator's user agents (it almost always is).

If you're in early development or your IdP doesn't expose a CORS-permissive JWKS endpoint, leave the field unset. The server-side validation on the next protected request remains the authoritative gate.

## Diff to apply

Existing OIDC deployments wanting to enable the client-side validation:

```fsharp
// Before — implicit None on the new field (the SDK default keeps validation off):
let oidcConfig =
    OidcUIConfig.defaults issuer clientId redirectUri

// After — opt in explicitly:
let oidcConfig = {
    OidcUIConfig.defaults issuer clientId redirectUri with
        ValidateIdToken = Some true
}
```

Consumers that hand-roll their `OidcUIConfig` record literal (as opposed to using `OidcUIConfig.defaults` or projecting from a companion config like `EntraExternalIdClientConfig.toOidcUIConfig`) must add the new `ValidateIdToken` field on the record literal — the compiler flags the missing field as a record-completeness error. Set it to `None` for byte-for-byte backward compatibility, or `Some true` to opt in.

```fsharp
// Hand-rolled record literal — explicit field needed:
let oidcConfig = {
    Issuer = "https://auth.example.com"
    ClientId = "my-app"
    RedirectUri = "https://app.example.com/auth/callback"
    Scopes = [ "openid"; "profile"; "email" ]
    PostLogoutRedirectUri = None
    ValidateIdToken = None      // ← was missing pre-3b.A; now explicit
}
```

## Security rationale

The validation pipeline is **operator-opt-in, not auto-enabled by the SDK** during the 0.3.x line. Three reasons:

1. **Surfacing a misconfiguration vs surfacing a CORS failure.** Some IdPs default their JWKS endpoint to the same CORS posture as their token endpoint (locked-down). For those deployments, flipping the default to `true` without operator awareness would turn every sign-in into a typed `DiscoveryFailed` error at the callback. Opt-in makes the consequences of enabling the path explicit.
2. **Defence in depth, not the primary gate.** Server-side `OidcAuthProvider.ValidateRequest` is the authoritative validation — every protected request goes through it. The client-side check shortens the time between "issuer issued a bad token" and "user sees a clear error" rather than replacing the server check.
3. **Algorithm-substitution defence.** Today the SDK ships only the RS256 verify path (mirrors server-side Phase 3.A's universal default). Wider algorithm support (ES256 / RS384 / RS512 / PS256) is a follow-on tracked alongside the server-side algorithm-list expansion. Until then, a token signed with anything other than RS256 surfaces as `IdTokenSignatureInvalid` — operators on EC-only IdPs should hold off opting in until the client-side algorithm dispatch lands.

The check is **not** a replacement for nonce validation (Cluster B1) — that check binds the id_token to *this* sign-in attempt regardless of signature, and remains mandatory and on-by-default. Phase 3b.A is additive.

## Verification

1. `dotnet build src/AuthProviders/OidcClient/OidcClient.fsproj` clean.
2. The Expecto runner `dotnet run --project src/ToolUp.Platform.Tests` passes the `OIDC id_token validation (Phase 3b.A)` testList — 21 cases covering the pure-F# claim validators, the JWT parser, the orchestrator with stubbed verifier (happy path + 5 failure modes), and the opt-in toggle contract.
3. Operationally: set `ValidateIdToken = Some true`, restart the client bundle, and complete a sign-in. Confirm the callback completes without error and the user lands signed in. Then point the deployment at a different issuer (same `ClientId`) and confirm the sign-in is rejected with the typed error rendered via `describeError`.
4. Manually inspect `sessionStorage` after sign-in: a key under `toolup-oidc-jwks:<jwks_uri>` carries the cached JWK set with a `fetchedAt` timestamp. On a second sign-in within 10 minutes, no new JWKS network request fires.

## Rollback

If a regression surfaces, set `ValidateIdToken = None` (or `Some false`). The callback handler skips the new pipeline and falls back to the byte-for-byte today's behaviour. The change is fully reversible at the deployment level; no on-disk state is migrated, and the `sessionStorage` JWKS cache is harmless if left behind (a stale entry expires within 10 minutes regardless).

## Consumers

Downstream OIDC consumers adopt by opting into the new validation field; the change is consumer-invisible until they do.

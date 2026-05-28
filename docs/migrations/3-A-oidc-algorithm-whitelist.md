# Phase 3.A — OIDC algorithm whitelist (consumer migration)

**What changes.** `OidcAuthProvider` widens its accepted JWS signature set beyond RS256. RS384, RS512, ES256, and PS256 are now first-class verify paths, gated behind an operator-chosen whitelist on `AuthConfig`. Every deployment that signed RS256 yesterday continues to validate RS256 today, byte-for-byte unchanged — the new field defaults to `None`, which resolves to the historical single-algorithm trust set `[RS256]`.

**Scope.** Server-side only — no client-side change. `OidcClient` (browser sign-in) does not look at signature algorithms; algorithm selection is the issuer's choice, surfaced to the server verifier through the `alg` field on each issued token.

## What was added

- `JwsAlgorithm` DU in `ToolUp.Platform.Core` — `RS256 | RS384 | RS512 | ES256 | PS256`. `HS256` is deliberately not a member: symmetric flows belong to `StaticJwtAuthProvider`, not OIDC. EdDSA / Ed25519 are deferred until customer demand surfaces.
- `AuthConfig.AcceptedAlgorithms: JwsAlgorithm list option` — operator-owned trust set. `None` resolves to `[RS256]`.
- `OidcAuthProvider` now dispatches signature verification on the typed algorithm; RSA / EC JWKS key materialisation is handled by the same `OidcAuthProvider.Jwks` code path that previously only understood RSA.

## When to opt in

Reach for `AcceptedAlgorithms = Some [...]` when:

- **AWS Cognito** — User Pool tokens default to RS256 today, but the OIDC OP for Cognito Identity Pools and the App Client variants for federated identity providers can emit ES256.
- **Firebase Auth** — federated sign-in (Google / Apple / Facebook) frequently rotates between RS256 and ES256 on the same issuer.
- **Okta / dynamic-client OIDC flows** — when the client registers a JWKS rather than a static signing key, the issuer may emit any algorithm announced in `id_token_signing_alg_values_supported`.
- **Hardened RSA deployments** — sites that ban PKCS#1 v1.5 padding by policy ship PS256 instead of RS256.

If you're on Entra External ID, Auth0 (default), Azure AD workforce, Keycloak, or Google Workspace — all of which sign RS256 unless explicitly reconfigured — leave `AcceptedAlgorithms` unset. The default is what you want.

## Diff to apply

Existing OIDC deployments wanting to widen the trust set:

```fsharp
// Before — RS256-only (the default; nothing in the record changes):
let authConfig = {
    Issuer = Some issuerUrl
    Audience = Some audience
    KeySource = JwksDiscovery issuerUrl
    TokenLocation = BearerHeader
    ClockSkewSeconds = None
    AcceptedAlgorithms = None    // ← was missing pre-3.A; now explicit
}

// After — interoperate with an IdP that issues ES256 alongside RS256:
let authConfig = {
    Issuer = Some issuerUrl
    Audience = Some audience
    KeySource = JwksDiscovery issuerUrl
    TokenLocation = BearerHeader
    ClockSkewSeconds = None
    AcceptedAlgorithms = Some [ RS256; ES256 ]
}
```

Consumers that hand-roll their `AuthConfig` record literal (as opposed to using `EntraExternalIdAuthProvider.fromEnv` or similar helpers) must add the new `AcceptedAlgorithms` field on the record literal — the compiler will flag the missing field as a record-completeness error. Set it to `None` for byte-for-byte backward compatibility.

## Security rationale

The whitelist is **operator-owned, not auto-widened by the SDK.** Even though every recognised algorithm carries its own cryptographic verify path, the SDK does not silently trust a new algorithm just because the inbound token's `alg` field names one. Three reasons:

1. **Algorithm-substitution attacks.** An issuer that today signs RS256 may, after a misconfiguration, start emitting tokens with a different `alg` while the SDK keeps fetching the same JWKS. The whitelist makes that drift visible as a `Unsupported algorithm: <name>` rejection rather than silently accepting the new shape.
2. **Defence in depth.** A compromised JWKS endpoint that injects an attacker-controlled EC key into the response cannot succeed against an `AcceptedAlgorithms = Some [RS256]` deployment — the algorithm gate rejects the ES256 token before the verify even runs.
3. **Auditability.** Operators can answer "what algorithms is this deployment willing to trust?" by reading a single line of config, not by reading SDK source. Widening the trust set is an explicit code change reviewable on its own.

## Verification

1. `dotnet build` clean across the workspace.
2. The Expecto runner `dotnet run --project src/ToolUp.Platform.Tests` passes all `AuthProviders` tests, including the five new Phase 3.A cases (`Default config accepts RS256 only`, the four per-algorithm Ok tests, and the ES256-against-RS256-whitelist rejection).
3. Operationally: start the deployment with `AcceptedAlgorithms = None`; confirm RS256 tokens from the existing IdP validate as before. Then set `Some [ RS256; ES256 ]`, restart, and confirm an ES256-signed token from a Cognito-shaped IdP validates while the RS256 path continues to work.
4. The `MockOidcServer`-driven contract suite continues to pass — that mock issuer signs RS256, so the path under the default whitelist is unchanged.

## Rollback

If a regression surfaces, set `AcceptedAlgorithms = None` (or omit the field — the compiler will demand it on the record literal, but `None` is the documented default). The provider falls back to the historical RS256-only path. The change is fully reversible at the deployment level; no on-disk state is migrated.

## Consumers

The migration is **N-A** for any consumer whose IdP issues RS256 tokens (the common case) — they inherit the SDK default. A consumer becomes affected only if and when its IdP migrates to a non-RS256 algorithm. The SDK-side change is consumer-invisible until the consumer explicitly chooses to widen its trust set.

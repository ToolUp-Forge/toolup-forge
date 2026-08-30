# Changelog — ToolUp.AuthProviders.Oidc.Client

All notable changes to the `ToolUp.AuthProviders.Oidc.Client` package (renamed from `ToolUp.AuthProviders.OidcClient` in 0.3.0 — see Phase 11.C.5 below) are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

- **Added** a bearer-token strategy. `OidcAppConfig` and `OidcUIConfig` each gain `BearerToken: BearerTokenKind option` (`AccessTokenBearer` | `IdTokenBearer`), deciding which of the two tokens an OIDC sign-in returns is stored and sent as the session's HTTP bearer. Exists for identity providers whose access tokens are **opaque** — Google always, since it has no dashboard audience knob — where an access-token bearer signs in successfully and then 401s on every API call, because the server-side provider validates a JWT against the issuer's JWKS and an opaque string has nothing to validate. The `id_token` is an ordinary RS256 JWT with `aud` = the client id, so the **unchanged** server-side provider validates it end to end.
- **Added** `PresetKind.defaultBearerToken` and `PresetKind.opaqueAccessTokenIsUnfixable`. Only `Google` defaults to `IdTokenBearer`: `Generic` and `Auth0` also report `expectsDecodableAccessToken = false`, but for reasons a deployment can act on (no provider knowledge; a configurable API audience), so the SDK does not pre-empt them. `OidcAppConfig.resolveBearerToken` gives an explicit consumer setting precedence over the preset default.
- **Added** `OidcStateMachine.decideBearerToken` — the pure decision shared by the callback and refresh paths. Under `IdTokenBearer` a token-endpoint response with no `id_token` is a typed failure rather than a silent fallback to the access token, on both paths.
- **Added** `OidcCoherenceValidator` rules 14 (ERROR — `id-token` bearer with `Audience <> ClientId`, which authenticates nobody) and 15 (WARNING — an unfixably-opaque preset left on the access-token strategy). Rule 11's provenance line now reports the resolved bearer.
- **BREAKING (record widening)** — consumers constructing `OidcUIConfig` or `OidcAppConfig` as a full record literal must add `BearerToken = None` for byte-for-byte prior behaviour. `OidcUIConfig.defaults`, `OidcAppConfig.create`, every `OidcPresets.*` constructor and every companion projection already supply it. Migration: [`docs/migrations/746-oidc-bearer-token-strategy.md`](../../../docs/migrations/746-oidc-bearer-token-strategy.md).

## [0.3.8] - 2026-05-31

- **Fixed** post-callback navigation in `handleCallback`: after persisting tokens the shell now does a full-document `location.replace` to the app **root** (`origin + "/"`) rather than the current pathname. The current pathname is the redirect URI (e.g. `/auth/callback`); reloading onto it re-satisfied `isCallbackUrl` on reboot, so `OidcShell` re-entered `handleCallback` with the `?code` already consumed and failed with `MissingCode`. Landing on the root makes the reboot take the `classifyStoredToken` branch, see the just-persisted token as fresh, and enter `SignedIn`.
- **Changed** `classifyStoredToken`: a 3-segment-shaped token whose payload cannot be base64-decoded + JSON-parsed is now classified as `OpaqueToken` (deferred to the server validator) rather than `StaleJwt` (which would trigger a doomed client-side refresh). Covers encrypted-body JWE / Microsoft Graph "nord" access tokens. Adds the `classifyStoredTokenWith` test seam and `OidcClassifyTokenTests` coverage.
- **Added** `diagnose : AuthError -> AuthDiagnostic` helper for structured, human-readable failure classification.
- **Added** `AuthTracer` — correlation-id stash + per-edge trace emits across the sign-in / callback / refresh state transitions for high-fidelity auth-flow logging.

## [0.3.0]

- **Renamed** package id `ToolUp.AuthProviders.OidcClient` → `ToolUp.AuthProviders.Oidc.Client` (Phase 11.C.5 — unifies the `.Client` suffix convention with `ToolUp.AIProviders.Claude.Client` and `ToolUp.AuthProviders.EntraExternalId.Client`). Consumer migration: rewrite the `<PackageVersion>` / `<PackageReference Include="...">` entry; F# `module` names inside the package are unchanged (still `ToolUp.AuthProviders.Oidc.OidcClient` / `ToolUp.AuthProviders.Oidc.OidcRegister` / etc.).

## [0.1.2]

Coordinated SDK release. No package-specific source changes since 0.1.0;
the version moved in lockstep with the `ToolUp.Sdk` meta-manifest.

## [0.1.0] - 2026-05-11

- Initial public release.

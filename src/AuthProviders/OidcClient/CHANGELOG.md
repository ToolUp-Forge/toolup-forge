# Changelog — ToolUp.AuthProviders.Oidc.Client

All notable changes to the `ToolUp.AuthProviders.Oidc.Client` package (renamed from `ToolUp.AuthProviders.OidcClient` in 0.3.0 — see Phase 11.C.5 below) are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

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

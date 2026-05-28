# Changelog — ToolUp.AuthProviders.Oidc.Client

All notable changes to the `ToolUp.AuthProviders.Oidc.Client` package (renamed from `ToolUp.AuthProviders.OidcClient` in 0.3.0 — see Phase 11.C.5 below) are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [0.3.0]

- **Renamed** package id `ToolUp.AuthProviders.OidcClient` → `ToolUp.AuthProviders.Oidc.Client` (Phase 11.C.5 — unifies the `.Client` suffix convention with `ToolUp.AIProviders.Claude.Client` and `ToolUp.AuthProviders.EntraExternalId.Client`). Consumer migration: rewrite the `<PackageVersion>` / `<PackageReference Include="...">` entry; F# `module` names inside the package are unchanged (still `ToolUp.AuthProviders.Oidc.OidcClient` / `ToolUp.AuthProviders.Oidc.OidcRegister` / etc.).

## [0.1.2]

Coordinated SDK release. No package-specific source changes since 0.1.0;
the version moved in lockstep with the `ToolUp.Sdk` meta-manifest.

## [0.1.0] - 2026-05-11

- Initial public release.

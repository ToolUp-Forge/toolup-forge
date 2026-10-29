# Changelog — ToolUp.AuthProviders.Oidc

All notable changes to the `ToolUp.AuthProviders.Oidc` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

### Added

- Phase 1139: sign-in failures in `OidcSsrSignIn` render a small HTML page
  (closed reason set, try-again link carrying the validated return URL,
  `no-store`) instead of bare `text/plain`; statuses are unchanged. New
  `SignInPage` (`defaults`, `withStylesheet`, `stylesheet`), `routesWithPage`
  and `withInteractiveSignInPage`. Additive: `OidcSsrSignInConfig` is
  untouched, so every existing construction of it still compiles, and
  `routes` / `withInteractiveSignIn` behave as before apart from the page body.
  Rejected alternative: a `Stylesheet` field on `OidcSsrSignInConfig`, which
  would break every full-literal construction (FS0764).

## [0.1.2]

### Changed

- Phase 3a: `OidcAuthProvider` is now validated against
  `IAuthProviderContract` via a mock `HttpMessageHandler` (test
  hardening; no public-surface change).

## [0.1.0] - 2026-05-11

- Initial public release.

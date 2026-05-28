# Changelog — ToolUp.Platform.Server

All notable changes to the `ToolUp.Platform.Server` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [0.1.2]

### Added

- `ServerConfig.IncludePlatformDefaults` — server-side opt-out for the
  `_platform` schema injection.
- Phase 37 (MVP): Peer-Bearer-Auth foundation — server composition and
  request handlers.
- Phase 9h (MVP): Data Subject Request orchestrator and extension points.

### Changed

- OAuth compose: the redirect-base validator is now registered as an
  instance rather than a factory.

## [0.1.0] - 2026-05-11

- Initial public release.

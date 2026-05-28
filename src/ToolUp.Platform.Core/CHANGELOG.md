# Changelog — ToolUp.Platform.Core

All notable changes to the `ToolUp.Platform.Core` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [0.1.2]

### Added

- `ServerConfig.IncludePlatformDefaults` — opt-out for the SDK
  `_platform` schema so deployments without monetary modules don't carry
  the platform default surface.
- Phase 37 (MVP): Peer-Bearer-Auth foundation — shared types and
  extension points for peer-bearer authentication.
- Phase 9h (MVP): Data Subject Request orchestrator — shared types and
  extension points for DSR fulfilment.

## [0.1.0] - 2026-05-11

- Initial public release.

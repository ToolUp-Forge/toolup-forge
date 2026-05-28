# Changelog — ToolUp.Platform.Build

All notable changes to the `ToolUp.Platform.Build` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [0.1.2]

### Added

- `AddHeaders` target — stamps (and, with `--check`, verifies for CI
  drift) the Apache-2.0 SPDX header on every Fable-packed source file.
  The file set is derived from the `PackagePath="fable"` content-include
  so new packed-source projects are covered automatically.

### Changed

- `Pack` target now writes to a single local
  `../local-nuget-feed` feed.

## [0.1.0] - 2026-05-11

- Initial public release.

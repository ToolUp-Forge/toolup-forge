# Changelog — ToolUp.Storage.Azure

All notable changes to the `ToolUp.Storage.Azure` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

### Fixed

- Vendor exceptions arriving wrapped in `AggregateException` are now
  matched through the wrapper (flatten + inner-exception match). Phase
  733 proved the class live HERE — the `DownloadRange` 416 arm sat dead
  and a fully-past-EOF range returned `Error "One or more errors
  occurred (…)"` instead of the contract's `Ok [||]`; the 404 arms were
  unaffected in effect only because their fall-through is also an
  `Error` — and fixed that arm; the Phase 734 sweep (same day,
  2026-08-27) threads every other `:? RequestFailedException` mapping
  through the same pattern: the 404s, and the ETag conditional-write
  seam (whose 412/409 test distinguishes `ETagMismatch` from
  `ConditionalWriteFailure` — an arm whose reachability changes an
  answer). Generic handlers report the inner exception's message rather
  than the `AggregateException` envelope.

## [0.1.2]

Coordinated SDK release. No package-specific source changes since 0.1.0;
the version moved in lockstep with the `ToolUp.Sdk` meta-manifest.

## [0.1.0] - 2026-05-11

- Initial public release.

# Changelog — ToolUp.Storage.GoogleCloud

All notable changes to the `ToolUp.Storage.GoogleCloud` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

### Fixed

- Vendor exceptions arriving wrapped in `AggregateException` are now
  matched through the wrapper (flatten + inner-exception match) at every
  `GoogleApiException` catch site. The direct type tests never fired on
  the wrapped shape (found by the first armed cloud-parity run,
  2026-08-27): `Delete` of a missing object returned `Error` instead of
  being idempotent, and a wrapped `PreconditionFailed` on the
  conditional-write seam would have surfaced as `ConditionalWriteFailure`
  rather than `ETagMismatch`. Error messages from generic handlers now
  carry the inner exception's message rather than
  "One or more errors occurred (…)".

## [0.1.2]

Coordinated SDK release. No package-specific source changes since 0.1.0;
the version moved in lockstep with the `ToolUp.Sdk` meta-manifest.

## [0.1.0] - 2026-05-11

- Initial public release.

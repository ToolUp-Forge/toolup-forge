# Changelog — ToolUp.Platform.NotificationChannels.Push.WebPush

All notable changes to the `ToolUp.Platform.NotificationChannels.Push.WebPush` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

### Fixed

- Vendor exceptions arriving wrapped in `AggregateException` are now
  matched through the wrapper (flatten + inner-exception match) — the
  Phase 734 sweep of the class the first armed cloud-parity run
  (2026-08-27) proved live in the AWS companions.
  `SendNotificationAsync` is a non-generic `Task` await — the
  highest-risk shape — and a wrapped `WebPushException` would have
  lost the 404/410 subscription-expiry (permanent) classification to
  the generic transient arm.

## [0.1.2]

Coordinated SDK release. No package-specific source changes since 0.1.0;
the version moved in lockstep with the `ToolUp.Sdk` meta-manifest.

## [0.1.0] - 2026-05-11

- Initial public release.

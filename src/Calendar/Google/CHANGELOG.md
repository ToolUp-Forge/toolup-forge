# Changelog — ToolUp.Calendar.Google

All notable changes to the `ToolUp.Calendar.Google` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

### Fixed

- `LinkResource` read the calendar through `calendars.get`, which Google does
  not authorise under the `calendar.events` scope the flow requests, so a real
  link failed with a 403 `ACCESS_TOKEN_SCOPE_INSUFFICIENT` (found by the first
  live probe, 2026-09-23). It now reads the user's `calendarList` entry, which
  the `calendar.calendarlist.readonly` scope the flow already requests for the
  health probe covers; an unreachable calendar still answers 404 and is still
  reported as `CalendarNotFound`. No scope was added.

### Added

- Initial release. `GoogleCalendarBridge` implements the Phase 20a
  `ICalendarBridge` seam over the Google Calendar v3 REST API with BCL
  `HttpClient` behind the platform's egress-policy handler — no Google
  client library. Push inserts under a deterministic event id (so a
  re-push updates instead of duplicating) and updates by
  read-merge-write, preserving what the calendar's owner set in Google.
  Recurrence rides Google's `recurrence` array; attendees become real
  Google attendees, with the pushed spelling carried through.
- Incremental pull: `SupportsIncrementalPull = true`; `since` is honoured
  through the stored sync token, or `updatedMin`, with a 410 fallback.
- `GoogleCalendarOAuth` — the `google-calendar` `IOAuthCredentialFlow`
  (Authorization Code + PKCE, `access_type=offline`, `prompt=consent`) on
  the platform OAuth substrate, and `GoogleCalendarTokenSource`, which
  resolves a bearer token per call through `IOAuthTokenRefresher` (or
  mints one per call without it), so a rotated credential needs no
  restart.
- Watch channels: `LinkResource` opens a channel authenticated by an
  HMAC token naming its scope and calendar; `GoogleCalendarChannels`
  ships the notification route (`handler` / `receive`) and the renewal
  job (`withChannelRenewal`), which renews expiring channels and pulls a
  link whose renewal failed.
- `GoogleCalendarBridgeHealth` — an `IHealthCheck` readiness probe over
  `calendarList.list`: a refused credential is `Unhealthy`, an
  unreachable Google is `Degraded`.

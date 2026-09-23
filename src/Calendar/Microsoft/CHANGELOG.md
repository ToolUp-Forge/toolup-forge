# Changelog — ToolUp.Calendar.Microsoft

All notable changes to the `ToolUp.Calendar.Microsoft` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

### Added

- Initial release. `MicrosoftGraphCalendarBridge` implements the Phase 20a
  `ICalendarBridge` seam over Microsoft Graph v1.0 with BCL `HttpClient`:
  `GET /me/calendars/{id}` to link, `POST` / `PATCH` / `DELETE` of
  `/me/calendars/{id}/events` to push, `calendarView/delta` to pull. No
  Graph SDK and no paid dependency.
- The booking rides each Graph event as two extended properties (the
  booking id and a snapshot of the pushed metadata and recurrence rule),
  which makes `Push` idempotent and a push → pull round-trip lossless
  while Outlook edits to native fields still win.
- `MicrosoftGraphOAuth` — the delegated `IOAuthCredentialFlow`
  (authorization code + PKCE against the v2.0 endpoint, refresh-token
  rotation written back), the per-call access-token resolution through
  `IOAuthTokenRefresher`, the `MicrosoftGraphCalendar` data-source Kind
  and `connectionId` convention, and `MicrosoftGraphCalendarSettings` +
  `fromEnv()` over `TOOLUP_MSGRAPH_CALENDAR_CLIENT_ID` / `_TENANT` /
  `_NOTIFICATION_URL` / `_ENDPOINT`.
- `MicrosoftGraphSubscriptions` — the notification route (`receive` /
  `handler`) answering Graph's validation handshake and verifying each
  notification's `clientState` before any pull; the hourly
  subscription-renewal job with its delta-polling fallback; and
  `compose` / `withSubscriptionRenewal`.
- `MicrosoftGraphCalendarBridgeHealth` — an `IHealthCheck` readiness probe
  (`GET /me/calendars?$top=1` with one connection's token). A refused
  credential is `Unhealthy`; an unreachable Graph is `Degraded`.
- Declared capabilities: webhooks when a notification URL is configured,
  no modification cursor (`since` is an event-time bound; the delta cursor
  is internal to the default window), five-minute poll floor.

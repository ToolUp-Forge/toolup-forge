# Changelog — ToolUp.Calendar.CalDAV

All notable changes to the `ToolUp.Calendar.CalDAV` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

### Added

- Initial release. `CalDAVCalendarBridge` implements the Phase 20a
  `ICalendarBridge` seam over RFC 4791: `PROPFIND` to link, `REPORT
  calendar-query` to pull, `PUT`/`DELETE` of `<collection>/<uid>.ics` to
  push. BCL `HttpClient` behind the platform's egress-policy handler; no
  vendor SDK and no paid dependency.
- `CalDAVSettings` + `CalDAVSettings.fromEnv()` reading
  `TOOLUP_CALDAV_URL` / `TOOLUP_CALDAV_USERNAME` /
  `TOOLUP_CALDAV_ENDPOINT`. The password is not configuration: it is read
  from `ISecretStore` per call, so rotation needs no restart.
- `CalDAVCalendarBridgeHealth` — an `IHealthCheck` readiness probe that
  `PROPFIND`s the configured base URL. A refused credential is
  `Unhealthy`; an unreachable server is `Degraded`, because a calendar
  outage must not restart the process.
- Declared capabilities: polling-only, no modification cursor,
  fifteen-minute poll floor.

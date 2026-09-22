# Changelog — ToolUp.Observability.Datadog

All notable changes to the `ToolUp.Observability.Datadog` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [0.23.0]

- Initial release (Phase 9w). `IDatadogReadbackApi` over three read-only
  Datadog REST endpoints — monitor state (`/api/v1/monitor`), log search
  (`/api/v2/logs/events/search`) and metric timeseries (`/api/v1/query`).
- Region-aware base URL across the five Datadog sites (`US`, `EU`, `US3`,
  `US5`, `AP1`).
- `DD-API-KEY` + `DD-APPLICATION-KEY` resolved from `ISecretStore` on every
  call, so a rotated key flows through with no restart.
- Failure returned as a typed `DatadogReadbackError` rather than thrown; the
  companion neither logs nor audits on its own behalf.

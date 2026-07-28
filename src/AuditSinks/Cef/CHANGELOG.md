# Changelog — ToolUp.AuditSinks.Cef

All notable changes to the `ToolUp.AuditSinks.Cef` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

- Initial release. `IAuditSink` companion rendering audit envelopes as CEF
  lines over UDP / TCP / TLS syslog, for SIEMs that consume Common Event
  Format (ArcSight, QRadar, LogRhythm, McAfee ESM).
- Explicit 1023-byte line cap with a parse-safe `cefTruncated=true` marker.
- Deterministic `externalId` dedup key so the dispatcher's whole-batch
  retries collapse at the SIEM rather than duplicating.
- Device identity (CEF header fields 1–3) configurable per deployment via
  `_platform/audit/cef.json`; collector endpoint via `ISecretStore`.

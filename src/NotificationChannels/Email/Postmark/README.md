# Postmark email notification companion (deferred)

This directory reserves the location for a future **Postmark**
`INotificationSink` implementation. Phase 6f shipped:

- `src/NotificationChannels/Email/Smtp/` — the no-paid-deps default
  (works against any SMTP server / mail relay).
- `src/NotificationChannels/Email/SendGrid/` — the worked example for
  the paid-service-key pattern with `ISecretStore` lookup.

A Postmark companion would land here as a third email backend with
the same shape as SendGrid:

- `PostmarkNotificationSink.fs` — implements `INotificationSink`
  with `Kind = "Email"`, `Provider = "Postmark"`.
- HTTP-direct `POST /email` (or `POST /email/withTemplate` for
  templated sends) against `https://api.postmarkapp.com`.
- Server token sourced from
  `ISecretStore.GetSecret("_platform", "POSTMARK_SERVER_TOKEN")`.
- Activated via `TOOLUP_TRANSACTIONAL_EMAIL=postmark`.

The contract test pack
(`src/ToolUp.Platform.Tests/Contracts/INotificationSinkContract.fs`)
binds against any concrete sink, so adding a Postmark companion needs
only the impl + a test binding.

Status: **deferred**. Phase 6f shipped two email companions; teams
needing Postmark today implement their own sink against
`INotificationSink` and register it via `withTransactionalSink`.
A future sub-phase opens this directory for production code when
the demand surfaces.

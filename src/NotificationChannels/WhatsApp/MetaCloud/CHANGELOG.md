# Changelog — ToolUp.NotificationChannels.WhatsApp.MetaCloud

All notable changes to the `ToolUp.NotificationChannels.WhatsApp.MetaCloud` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

### Added

- Initial release (Phase 6f.C). `MetaWhatsAppCloudNotificationSink` — a
  `SinkKind.WhatsApp` `INotificationSink` (provider `meta-cloud`) over the
  Meta WhatsApp Business Cloud API: template messages (components split by
  the `IWhatsAppTemplateRegistry` arity, default language from the
  registry) and free-form text messages, one request per recipient,
  `CorrelationId` forwarded as `biz_opaque_callback_data`. BCL `HttpClient`
  behind the platform's egress-policy handler; no vendor SDK.
- `MetaWhatsAppSettings` + `MetaWhatsAppSettings.fromEnv()` reading
  `TOOLUP_META_WHATSAPP_PHONE_NUMBER_ID` / `_GRAPH_API_VERSION` (pinned
  default `v26.0`) / `_ENDPOINT`. The System User access token is read from
  `ISecretStore` (`_platform` / `meta_whatsapp_access_token`) on every send.
- `MetaCloudInbound` — an `IInboundWebhookHandler` of kind `whatsapp-meta`
  on the `ToolUp.Webhooks` substrate: customer messages stamp
  `ExternalContact.LastInboundUtc` and append a `_platform.whatsapp.inbound`
  event; `failed` statuses append `NotificationDeliveryFailed`; per-item
  dedup through `IWebhookDedupStore`; `routes` answers Meta's `GET`
  callback verification and mounts the substrate's verified `POST` route
  with the `X-Hub-Signature-256` scheme.
- `MetaCloudHealth` readiness probe (`GET /{version}/{phone-number-id}`) and
  `MetaCloudValidator` preflight (numeric phone number id, token, app
  secret when inbound is registered, HTTPS `PublicBaseUrl` in production).

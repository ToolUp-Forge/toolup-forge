# Changelog — ToolUp.NotificationChannels.WhatsApp.Twilio

All notable changes to the `ToolUp.NotificationChannels.WhatsApp.Twilio` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

### Added

- Initial release (Phase 6f.B): `TwilioWhatsAppNotificationSink`, an
  `INotificationSink` for `SinkKind.WhatsApp` over the Twilio Messages
  REST API — content-template sends (`ContentSid` + numbered
  `ContentVariables`) and free-form sends (`Body`), BCL `HttpClient` only.
  `TwilioWhatsAppSettings` (`fromEnv` over `TOOLUP_TWILIO_ACCOUNT_SID`,
  `TOOLUP_TWILIO_WHATSAPP_FROM`, `TOOLUP_TWILIO_WHATSAPP_CONTENT_SIDS`,
  `TOOLUP_TWILIO_ENDPOINT`), the `TwilioHealth` readiness probe (a `GET` of
  the account resource) and the `TwilioValidator` preflight.

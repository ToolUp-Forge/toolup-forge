# Twilio transactional SMS sink (Phase 6f)

SMS backend over the
[Twilio Messages REST API](https://www.twilio.com/docs/messaging/api).
Implements `INotificationSink` with `Kind = "Sms"`. Pure HTTP — no
Twilio NuGet SDK dependency.

## Activation

```bash
TOOLUP_TRANSACTIONAL_SMS=twilio
TOOLUP_TWILIO_ACCOUNT_SID=AC…
TOOLUP_TWILIO_FROM=+15555550123  # E.164 or alphanumeric sender ID
```

The auth token comes from `ISecretStore` under
`_platform/TWILIO_AUTH_TOKEN`. Account Sid is half-public (Twilio
support / billing portals show it without redaction) so it lives in
settings, not the secret store.

## Per-recipient send

Twilio's API takes one recipient per request. The sink iterates the
resolved `PhoneNumber` list serially. The first non-success result
short-circuits the loop and is returned to the dispatcher; the
dispatcher's retry budget covers the entire envelope (including the
already-delivered prefix). For at-most-once delivery without
duplicates, callers should set `CorrelationId` and
[Twilio idempotency keys](https://www.twilio.com/docs/usage/idempotency).

## Failure classification

| HTTP response | `SinkResult` |
|---|---|
| 200 / 201 | `Delivered` (Twilio `MessageSid` parsed from response JSON) |
| 429 | `TransientFailure` |
| 5xx | `TransientFailure` |
| Other 4xx | `PermanentFailure` |
| Network / timeout | `TransientFailure` |

## SMS body length

Twilio bills per 160-character GSM-7 segment (or 70-char UCS-2 for
non-Latin scripts). The sink doesn't truncate — `SmsEnvelope.Body`
flows verbatim to Twilio. Callers wanting hard-capped messages
truncate at the publisher.

# Twilio transactional WhatsApp sink (Phase 6f.B)

WhatsApp backend over the
[Twilio Messages REST API](https://www.twilio.com/docs/whatsapp/api).
Implements `INotificationSink` with `Kind = SinkKind.WhatsApp` and
`Provider = "Twilio"`. Pure HTTP — no Twilio NuGet SDK dependency, the
same shape as the Twilio SMS companion: one account, one auth token, one
Messages endpoint, with a `whatsapp:` prefix on the `From` / `To`
addresses.

The WhatsApp rules — the 24-hour customer-care window, template
registration and template arity — are enforced by the platform's shared
WhatsApp arm before any sink runs (see
[`docs/companions/notification-channels.md`](../../../../docs/companions/notification-channels.md)
§ WhatsApp). This sink sends what it is handed and classifies what Twilio
answers.

## Activation

```bash
TOOLUP_TWILIO_ACCOUNT_SID=AC…                    # shared with the SMS sink
TOOLUP_TWILIO_WHATSAPP_FROM=+14155238886         # E.164; the Twilio sandbox number shown
TOOLUP_TWILIO_WHATSAPP_CONTENT_SIDS=appointment_reminder@en_GB=HX…,appointment_reminder=HX…
TOOLUP_TWILIO_ENDPOINT=…                         # optional Messages-endpoint override (tests / mocks)
```

The auth token comes from `ISecretStore` under `_platform/TWILIO_AUTH_TOKEN`
— the same secret the SMS sink reads — and is read fresh on every send, so
a rotation takes effect at once. The account SID is half-public (Twilio
shows it unredacted in its console), so it lives in settings.

Register the sink with `ServerApp.withTransactionalSink`, the readiness
probe (`TwilioHealth.create`) with `ServerApp.withHealthCheck`, and the
preflight (`TwilioValidator.create`) with `ServerApp.withConfigValidator`;
the channel guide above shows the three calls together. Registering a
second `SinkKind.WhatsApp` sink fails at compose time. The companion is
fully optional: a server project that does not reference it builds and
runs unchanged.

## Template messages and content SIDs

A WhatsApp business-initiated message must be a template the vendor has
approved. The platform addresses a template by its vendor-neutral NAME
(the name in the deployment's `IWhatsAppTemplateRegistry`); Twilio
addresses it by a **content SID** (`HX…`), one per approved
language. `TwilioWhatsAppSettings.ContentSids` maps one onto the other:

| Key | Used when |
|---|---|
| `name@language` (e.g. `appointment_reminder@en_GB`) | the envelope's `TemplateLanguage` matches |
| `name` | no language-specific entry matches, or the envelope names no language |

A template send whose name has no entry fails permanently with
`twilio_content_sid_not_configured` and sends nothing — the deployment
registered a template it never mapped onto a Twilio resource.

On the wire a template send carries `ContentSid` and, when the template
takes parameters, `ContentVariables` — a JSON object keyed by placeholder
number: the envelope's flat `TemplateParameters` (header, then body, then
buttons) become `{"1": …, "2": …}` in order. A free-form send (inside the
window) carries `Body` instead. `Metadata` and `CorrelationId` are not
forwarded: the Messages API has no field for either.

## Sandbox setup and content-template creation

1. In the Twilio console, join the **WhatsApp sandbox** (Messaging → Try it
   out → Send a WhatsApp message) from the handset you will test with. The
   sandbox sender is `+14155238886`; a production deployment uses its own
   WhatsApp-enabled sender instead.
2. Create the template in the **Content Template Builder** (Messaging →
   Content Template Builder), with `{{1}}`, `{{2}}`, … placeholders in the
   order the registry's arity describes, and submit it for WhatsApp
   approval. Copy its `HX…` SID into `TOOLUP_TWILIO_WHATSAPP_CONTENT_SIDS`.
3. Register the same template NAME, languages and arity in the
   deployment's `IWhatsAppTemplateRegistry`, so the server admits the send.

## Opt-in flow

A WhatsApp number resolves only through
`INotificationAddressBook.ResolveWhatsApp`. For an external contact that
means the contact's `OptionalWhatsAppNumber` under a live
`SinkKind.WhatsApp` opt-in — an SMS opt-in does not unlock it, even for the
same number. A contact with no WhatsApp consent is refused and audited by
the consent filter before the envelope reaches this sink; a platform user
resolves no WhatsApp number in the default address book. A recipient that
resolves to nothing is skipped (`no_addressable_recipients`).

## Per-recipient send

One recipient per request, serially. The first non-success result stops
the loop and is returned to the dispatcher, whose retry budget covers the
whole envelope (including any already-delivered prefix).

## Failure classification

| Response | `SinkResult` |
|---|---|
| 200 / 201 | `Delivered` (Twilio's message SID parsed from the response) |
| 429 | `TransientFailure` |
| 5xx | `TransientFailure` |
| Other 4xx | `PermanentFailure` (e.g. Twilio 63016, outside the window) |
| Network / timeout | `TransientFailure` |
| Template not mapped to a content SID | `PermanentFailure` (`twilio_content_sid_not_configured`) |
| Auth token missing from the secret store | `PermanentFailure` |

The readiness probe `GET`s the account resource
(`/2010-04-01/Accounts/{Sid}.json`) with the send's own credential: 401 /
403 / 404 is `Unhealthy`; a 5xx, 429 or transport failure is `Degraded`,
so a Twilio outage does not take the process out of rotation. The
preflight checks, without calling Twilio, that the token resolves, the
account SID is non-empty and the sender is E.164.

## Probe recipe (out of suite, operator-run)

The test suite never talks to Twilio. To see a real delivery, with a
sandbox-joined handset:

1. Set the four variables above (sandbox sender, a mapped content SID) and
   put the auth token in the secret store.
2. File an external contact with the handset's number as its WhatsApp
   number and record a `SinkKind.WhatsApp` opt-in on it.
3. Enable `whatsapp.enabled` in the team's `_platform.notification_prefs`.
4. Publish a `TransactionalWhatsApp` template envelope to that contact.
   The message arrives on the handset, and the audit trail shows
   `NotificationSent` for the envelope.

# ToolUp.NotificationChannels.WhatsApp.MetaCloud

A `SinkKind.WhatsApp` `INotificationSink` that talks straight to the **Meta WhatsApp Business Cloud
API** (the Graph API's `/{phone-number-id}/messages`) over BCL `HttpClient` — no vendor SDK, no paid
dependency — plus an **inbound webhook handler** for customer messages and delivery statuses, built
on the `ToolUp.Webhooks` substrate.

It consumes the vendor-neutral `TransactionalWhatsApp` envelope, so it is interchangeable with any
other WhatsApp sink: the platform decides *whether* a message may be sent (consent, the 24-hour
customer-care window, template registration and arity — see the WhatsApp section of
`docs/companions/notification-channels.md`); this package decides only *how* Meta is asked to send
it. A deployment registers at most one WhatsApp sink.

## What you need from Meta

1. **A Meta App** (type *Business*) with the **WhatsApp** product added, attached to a **WhatsApp
   Business Account** and a verified business phone number. The App dashboard's *WhatsApp → API
   Setup* page shows the number's **phone number ID** — a long numeric id, *not* the phone number.
2. **A System User access token.** In Business Settings → *Users → System users*, create a system
   user, assign it the WhatsApp Business Account and the App, and generate a token with the
   `whatsapp_business_messaging` and `whatsapp_business_management` permissions. Choose *never
   expires* for a long-lived token. (The temporary token on the API Setup page expires in 24 hours
   and is for experiments only.)
3. **Approved message templates** for every business-initiated message. Templates are created in
   WhatsApp Manager and reviewed by Meta; approval usually takes minutes but can take **up to 24–48
   hours**, and a rejected template must be edited and resubmitted. Plan template work ahead of the
   release that needs it.
4. For inbound traffic: the **App secret** (App settings → Basic) and a **verify token** of your own
   choosing.

## Configuration

| Setting | Where | Notes |
|---|---|---|
| `TOOLUP_META_WHATSAPP_PHONE_NUMBER_ID` | env / manifest | Required. The numeric phone number ID. |
| `TOOLUP_META_WHATSAPP_GRAPH_API_VERSION` | env / manifest | Optional. Defaults to the pinned `MetaWhatsAppSettings.DefaultGraphApiVersion` (`v26.0`). Override to move ahead of, or hold behind, the pin without a package upgrade. |
| `TOOLUP_META_WHATSAPP_ENDPOINT` | env / manifest | Optional. Replaces `https://graph.facebook.com`; the version and path are still appended. For a proxy or a test double. |
| `meta_whatsapp_access_token` | `ISecretStore`, scope `_platform` | Required. The System User token. |
| `meta_whatsapp_app_secret` | `ISecretStore`, scope `_platform` | Required when the inbound handler is registered — the HMAC key Meta signs deliveries with. |
| `meta_whatsapp_verify_token` | `ISecretStore`, scope `_platform` | Needed for the dashboard's callback verification. |

`MetaWhatsAppSettings` carries no secret and is safe to log.

### Token rotation

The access token is read from `ISecretStore` on **every send** (and every health probe), never
cached. Rotation is manual and transparent: generate a new System User token, write it to the
secret store, then revoke the old one. In-flight sends that already read the old token finish with
it; the next send picks up the new one — no restart. A revoked or expired token surfaces as a
`PermanentFailure` on send and an `Unhealthy` readiness probe, never as a silent drop.

## Composition

```fsharp skip=fragment
open ToolUp.Platform.NotificationChannels.WhatsApp

let settings = MetaCloud.MetaWhatsAppSettings.fromEnv ()

let sink =
    MetaCloud.MetaWhatsAppCloudNotificationSink.create addressBook secretStore templateRegistry settings (Some logger)

app
|> ServerApp.withTransactionalSink sink
|> ServerApp.withHealthCheck (MetaCloudHealth.create secretStore settings)
|> ServerApp.withConfigValidator (MetaCloudValidator.create secretStore settings serverConfig true)
```

`addressBook` resolves each recipient's WhatsApp number through
`INotificationAddressBook.ResolveWhatsApp`, which releases an external contact's number only under a
live WhatsApp opt-in. `templateRegistry` is the deployment's `IWhatsAppTemplateRegistry` — the same
one the send policy checked — from which the sink reads each template's per-component arity to build
Meta's `components` array, and its default language when the envelope names none.

### Message shapes

- **Template** (`TemplateName = Some _`): `type: "template"` with `name`, `language.code` and
  `components`. `TemplateParameters` is flat — header, then body, then button parameters — and is
  split by the registered arity. Header parameters are text parameters; each button parameter
  addresses its own dynamic-URL button, in order (`index` 0, 1, …).
- **Free-form** (`Body = Some _`): `type: "text"`. The 24-hour window is enforced before the sink
  runs, so the sink sends what it is handed.
- `CorrelationId` is sent as `biz_opaque_callback_data`; Meta echoes it on every status callback.
- `Metadata` is not forwarded — the Cloud API has no field for free-form tags.

The Cloud API takes one recipient per request and has no batch endpoint, so the sink sends serially
and stops at the first failure; the dispatcher's retry budget applies to the envelope. `429`, `5xx`
and Meta's throttling / temporarily-unavailable error codes are `TransientFailure`; every other
refusal is `PermanentFailure` with Meta's error body preserved for support tickets.

## Inbound webhooks

The handler is an `IInboundWebhookHandler` of kind `whatsapp-meta` for the `ToolUp.Webhooks`
registry. `MetaCloudInbound.routes` mounts it at `/webhooks/whatsapp-meta`: it answers Meta's `GET`
callback verification (`hub.verify_token`, compared in constant time) and hands every `POST` to the
substrate's own verify → dedup → dispatch route with the Meta signature scheme
(`X-Hub-Signature-256`, HMAC-SHA256 over the raw body, lower-case hex). A bad or missing signature
is rejected before the handler runs.

```fsharp skip=fragment
let dedup = InMemoryWebhookDedupStore() :> IWebhookDedupStore // durable store for multi-instance

let inbound =
    MetaCloudInbound.create contactStore dedup eventStore auditLog (MetaCloudInbound.fixedScopes [ "team-acme" ]) (Some logger)

let registry = WebhookRegistry.ofList [ inbound ]

// Before any general `WebhookRoutes.routes` mount — this one is gated on its own path.
choose [ MetaCloudInbound.routes secretStore dedup registry; (* … *) ]
```

- **Customer messages** stamp `ExternalContact.LastInboundUtc` (Meta's message timestamp; never moved
  backwards) on every contact in the `contactScopes ()` address books whose WhatsApp number matches
  the sender — that is what opens the 24-hour window for free-form replies — and append one
  `_platform.whatsapp.inbound` event (`WhatsAppInboundMessage`: contact id, message id, type, and the
  text of a text message) to each matched contact's scope.
- **`failed` statuses** append `NotificationDeliveryFailed` (provider `meta-cloud`, Meta's error code
  and title, the echoed correlation id) in the matched contacts' scopes, or `_platform` when no
  contact holds the number. `sent` / `delivered` / `read` are acknowledged and ignored.
- **Dedup.** Meta sends no delivery-id header, so the handler claims each message id and each failed
  status in the `IWebhookDedupStore` itself: a redelivered batch is a no-op item by item.

### Subscribing the webhook

In the App dashboard, *WhatsApp → Configuration → Webhook*: set the callback URL to
`{PublicBaseUrl}/webhooks/whatsapp-meta` (**HTTPS only** — Meta refuses anything else, and the
validator refuses a non-HTTPS `PublicBaseUrl` in production), enter the verify token, press *Verify
and save*, then subscribe the **`messages`** field. The same subscription can be made through the
Graph API (`POST /{app-id}/subscriptions` with `object=whatsapp_business_account`,
`callback_url`, `verify_token`, `fields=messages`, authenticated with an app access token) as a
deployment runbook step; this package ships no helper for it.

### Opt-in

Meta requires a recipient's opt-in before a business messages them. Record it as a WhatsApp consent
on the `ExternalContact` (`IExternalContactApi.RecordOptIn` with `SinkKind.WhatsApp` and the Article
7 source evidence); until one is live, `ResolveWhatsApp` yields nothing and the send is refused
upstream. An inbound message is not an opt-in.

## Health and preflight

- `MetaCloudHealth.create` — readiness probe: `GET /{version}/{phone-number-id}` with the token.
  `401`/`403` (token rejected) and `400`/`404` (wrong phone number id) are `Unhealthy`; `429`, `5xx`
  and network errors are `Degraded`. Read-only and unbilled.
- `MetaCloudValidator.create` — offline preflight: numeric phone number id, the token resolves, and
  with the inbound handler registered, the app secret resolves and `PublicBaseUrl` is HTTPS
  (an error in a production-shaped deployment, a warning otherwise).

## Probe recipe (out of suite, operator-run)

The test suite never touches Meta. To prove a real deployment end to end:

1. Set the three secrets and `TOOLUP_META_WHATSAPP_PHONE_NUMBER_ID`; confirm `/ready` reports
   `transactional_sink:meta-whatsapp` healthy.
2. With an **approved** template registered in the `IWhatsAppTemplateRegistry`, send a
   `TransactionalWhatsApp` template message to a consented test contact; the audit trail records
   `NotificationSent` with the `wamid` as the vendor message id.
3. Watch the webhook: `sent` then `delivered` statuses arrive (acknowledged, no audit row).
4. Reply from the handset; the contact's `LastInboundUtc` moves and a `_platform.whatsapp.inbound`
   event appears — a free-form message to the contact is now admitted for 24 hours.

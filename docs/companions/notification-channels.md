# Notification channel companions

The Platform's notification subsystem covers two distinct concerns:

1. **Real-time pub/sub** — `INotificationChannel` carries notifications over SSE to subscribed clients (`SystemMessage`, `JobProgress`, `RefreshData`, etc.).
2. **Transactional delivery** — `INotificationSink` ships email / SMS / push out-of-band so PII doesn't cross the pub/sub topics.

This page is a cross-cutting overview of the shipped notification companions. For full details on the `INotificationChannel` contract + the transactional dispatcher, see [`platform/events.md`](../platform/events.md) + [`platform/architecture.md`](../platform/architecture.md) "Notifications" section.

## Real-time pub/sub (`INotificationChannel`)

The SDK ships one channel companion:

### `ToolUp.NotificationChannels.Redis`

Use when:
- Multi-instance deployments — SSE subscribers and publishers may live on different nodes.

Setup (env-var-driven):

```bash
TOOLUP_NOTIFICATION_CHANNEL=redis
TOOLUP_REDIS_CONNECTION=localhost:6379
```

The reference deployment reads these env vars at startup and registers the Redis channel:

```fsharp
open ToolUp.Platform.NotificationChannels.Redis

let notificationChannel =
    RedisNotificationChannel.fromConnectionString "localhost:6379" None

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withNotifications notificationChannel
// ... the deployment's other ServerApp.with* calls ...
|> ServerApp.run
```

Per-scope topic isolation is structural — one topic per `ScopeId`. There's no cross-tenant subscribe path. Subscribers receive events for their scope only.

Scaling characteristics:
- Pub/sub throughput limited by Redis's PUB/SUB (~tens of thousands per second).
- Subscription state stored in Redis; survives subscriber-side reconnects.
- The default `InMemoryNotificationChannel` is single-instance only — multi-instance requires the Redis companion (or a future Kafka / NATS companion).

### Default `InMemoryNotificationChannel`

Built into `ToolUp.Platform.Server`. Used when no companion is wired. Works for single-instance deployments; degrades silently to "subscribers on other nodes never see the event" in multi-instance.

`/dev/inspect` shows the active channel. Operators verify in production that Redis is wired before scaling out.

## Transactional delivery (`INotificationSink`)

Three categories: email, SMS, push. Each has one or more vendor companions.

> **The package id is not the module path.** The headings below name the **NuGet package** (`ToolUp.NotificationChannels.Email.Smtp`); the **F# module you `open`** carries an extra segment — `ToolUp.Platform.NotificationChannels.Email.Smtp`. Same for the Redis channel above. Copying the heading into an `open` is the mistake to avoid; take the `open` from the code block.

### Email — `ToolUp.NotificationChannels.Email.Smtp`

Generic SMTP via MailKit. Vendor-agnostic (works with Mailgun, Postmark, Amazon SES, in-house mail relay, etc.).

Setup:

```bash
TOOLUP_TRANSACTIONAL_EMAIL=smtp
TOOLUP_SMTP_HOST=smtp.example.com
TOOLUP_SMTP_PORT=587
TOOLUP_SMTP_USERNAME=...
TOOLUP_SMTP_PASSWORD=...
TOOLUP_SMTP_FROM_EMAIL=noreply@example.com
TOOLUP_SMTP_FROM_NAME="My App"
```

**This is the one shipped sink that does not take an `ISecretStore`.** The SASL password is a field on `SmtpSettings` (`Password: string option`), and `SmtpSettings.fromEnv ()` reads it from `TOOLUP_SMTP_PASSWORD`. A deployment that wants the password out of the environment builds the record itself — read the value from `ISecretStore` at compose time and set `Password`. Unlike the API-keyed sinks below, rotation is therefore not per-call: a rotated SMTP password needs the settings rebuilt.

```fsharp
open ToolUp.Platform.NotificationChannels.Email.Smtp

// `create` takes the address book first and already returns
// `INotificationSink`; `SmtpSettings.fromEnv ()` reads the variables above.
let sink = SmtpNotificationSink.create addressBook (SmtpSettings.fromEnv ()) logger

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withTransactionalSink sink
// ... the deployment's other ServerApp.with* calls ...
|> ServerApp.run
```

Use when:
- Operator has SMTP credentials to a mail relay.
- Lowest-friction setup; works with any SMTP-compliant provider.

### Email — `ToolUp.NotificationChannels.Email.SendGrid`

SendGrid API (pure HTTP REST against `api.sendgrid.com/v3/mail/send`).

Setup:

```bash
TOOLUP_TRANSACTIONAL_EMAIL=sendgrid
TOOLUP_SENDGRID_FROM_EMAIL=noreply@example.com
TOOLUP_SENDGRID_FROM_NAME="My App"
```

API key in `ISecretStore`, key `SENDGRID_API_KEY`.

```fsharp
open ToolUp.Platform.NotificationChannels.Email.SendGrid

let sink =
    SendGridNotificationSink.create addressBook secretStore (SendGridSettings.fromEnv ()) logger

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withTransactionalSink sink
// ... the deployment's other ServerApp.with* calls ...
|> ServerApp.run
```

Use when:
- Templates are managed in SendGrid (operators edit; code references by template ID).
- SendGrid's deliverability features (IP reputation, link tracking) are required.

### Email — `ToolUp.NotificationChannels.Email.Postmark` (deferred)

Directory reserved at `src/NotificationChannels/Email/Postmark/README.md`. Implementation deferred; ships when there's customer demand.

### SMS — `ToolUp.NotificationChannels.Sms.Twilio`

Twilio API (pure HTTP REST against `api.twilio.com/2010-04-01/Accounts/...`).

Setup:

```bash
TOOLUP_TRANSACTIONAL_SMS=twilio
TOOLUP_TWILIO_ACCOUNT_SID=AC...
TOOLUP_TWILIO_FROM_NUMBER=+14155551234
```

Auth token in `ISecretStore`, key `TWILIO_AUTH_TOKEN`.

```fsharp
open ToolUp.Platform.NotificationChannels.Sms.Twilio

let sink =
    TwilioNotificationSink.create addressBook secretStore (TwilioSettings.fromEnv ()) logger

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withTransactionalSink sink
// ... the deployment's other ServerApp.with* calls ...
|> ServerApp.run
```

### Push — `ToolUp.NotificationChannels.Push.WebPush`

RFC 8030 + VAPID via the `WebPush` NuGet package. Browser-side Service Worker handles incoming pushes.

Setup:

```bash
TOOLUP_TRANSACTIONAL_PUSH=webpush
TOOLUP_VAPID_SUBJECT=mailto:admin@example.com
TOOLUP_VAPID_PUBLIC_KEY=...   # generated once; safe to expose
```

Private key in `ISecretStore`, key `VAPID_PRIVATE_KEY`.

```fsharp
open ToolUp.Platform.NotificationChannels.Push.WebPush

let sink =
    WebPushNotificationSink.create addressBook secretStore (WebPushSettings.fromEnv ()) logger

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withTransactionalSink sink
// ... the deployment's other ServerApp.with* calls ...
|> ServerApp.run
```

Browser side: register a Service Worker (`examples/sw.js` template ships with the companion). The Service Worker handles `push` events and shows OS notifications.

## How the dispatcher works

`TransactionalDispatcher` is a `BackgroundService` that drains a bounded `Channel<NotificationEnvelope>`. Per envelope:

1. Looks up the user's contact details via `INotificationAddressBook` (default: blob-backed `BlobBackedNotificationAddressBook` reads from `_platform/contacts/{scopeId}/{userId}.json`).
2. Resolves the vendor-neutral address (`EmailAddress` / `PhoneNumber` / `PushToken`).
3. Checks the per-team `_platform.notification_prefs` kill switches.
4. Routes by `Kind` (`SinkKind.Email` / `SinkKind.Sms` / `SinkKind.Push of PushVariant`) to the matching registered `INotificationSink`.
5. Calls `sink.Send(scopeId, envelope)` and reads the returned `SinkResult` — `Delivered` / `Skipped` / `TransientFailure` / `PermanentFailure`.
6. Emits `NotificationSent` or `NotificationDeliveryFailed` audit event under `_platform.notifications`.

PII (email addresses, phone numbers, push tokens) NEVER crosses pub/sub topics — only `userId`s flow through the channel; addresses resolve at dispatch time via the address book.

Duplicate-`Kind` sink registration is rejected at compose time. If you want fallback (Postmark primary, SES secondary), wrap them in a `ChainedSink` composition you write yourself.

## Contact address book

`INotificationAddressBook` resolves `userId` → vendor-neutral addresses:

```fsharp
type INotificationAddressBook =
    abstract ResolveEmail: userId: string * scopeId: string -> Async<EmailAddress option>
    abstract ResolvePhone: userId: string * scopeId: string -> Async<PhoneNumber option>
    abstract ResolvePushTokens: userId: string * scopeId: string -> Async<PushToken list>
```

One method per channel rather than one call returning a bundle: a dispatch only ever needs the
address for the kind it is delivering, so a sink that sends SMS never causes an email lookup — and an
implementation backed by an external directory can answer the cheap question cheaply.

Default `BlobBackedNotificationAddressBook` reads from `_platform/contacts/{scopeId}/{userId}.json`. Manually populated by the operator or via your app's profile-management UI.

Custom impls can integrate with external identity providers (Active Directory, Cognito user pools, etc.) — implement the interface, register via DI.

## Notification preferences

Per-team `_platform.notification_prefs` blob stores per-kind kill switches:

```json
{
  "EmailEnabled": true,
  "SmsEnabled": false,
  "PushEnabled": true
}
```

The dispatcher checks before routing. `SmsEnabled = false` means SMS envelopes are dropped (with a `NotificationDropped` audit event) — useful for teams that opt out of SMS to control costs.

Admin UI for preferences is not built-in; deployments add a module that writes the prefs blob.

## Real-time vs transactional

Same Notification cases ride both paths:

```fsharp
type Notification =
    | SystemMessage of level: SystemMessageLevel * text: string          // pub/sub
    | JobCompleted of jobId: Guid * status: string * resultLink: string option  // pub/sub
    | DataRefreshed of dataTypeId: string * scopeId: string              // pub/sub
    | TeamActivity of kind: string * summary: string                     // pub/sub
    | ModuleAction of moduleId: string * actionKey: string * payloadJson: string  // pub/sub
    | CustomNotification of key: string * payloadJson: string            // pub/sub
    | MembershipChanged of MembershipChangedPayload                      // pub/sub, platform-reserved
    | TransactionalEmail of EmailEnvelope                                // out-of-band via INotificationSink
    | TransactionalSms of SmsEnvelope                                    // out-of-band via INotificationSink
    | MobilePush of PushEnvelope                                         // out-of-band via INotificationSink
```

Payloads are JSON **strings**, not a parsed `JsonValue` — the wire shape stays opaque to the SDK so a subscriber decodes it with its own converter set.

`DispatchingNotificationChannel` decorator routes by case:
- `SystemMessage` / `JobCompleted` / `DataRefreshed` / `TeamActivity` / `ModuleAction` / `CustomNotification` / `MembershipChanged` → publish over `INotificationChannel` (pub/sub).
- `TransactionalEmail` / `TransactionalSms` / `MobilePush` → enqueue to `TransactionalDispatcher` (out-of-band).

The decorator is auto-wired by `ServerApp.run` when transactional sinks are registered. Apps without sinks skip the dispatcher entirely.

## Activation

Per-deployment env-var-driven activation:

```bash
TOOLUP_TRANSACTIONAL_EMAIL=smtp        # or sendgrid; or unset to disable
TOOLUP_TRANSACTIONAL_SMS=twilio        # or unset
TOOLUP_TRANSACTIONAL_PUSH=webpush      # or unset
```

The reference deployment reads these and wires the corresponding sinks. For explicit programmatic wiring, use `ServerApp.withTransactionalSink` directly.

Deployments without any sinks skip the dispatcher hosted-service entirely — zero runtime cost.

## Writing a new sink

For a vendor not covered (Postmark, Mailgun, AWS SNS, Firebase Cloud Messaging, etc.):

```fsharp skip=fragment
module MyVendor.NotificationSink

open System.Net.Http
open ToolUp.Platform

type MyVendorEmailSink(settings: MyVendorSettings, secretStore: ISecretStore, httpClient: HttpClient) =
    interface INotificationSink with
        // A `SinkKind` DU case, not a `NotificationKind` wire string.
        member _.Kind = NotificationKind.SinkKind.Email
        // Free-form vendor label, surfaced in audit and /dev/inspect.
        member _.Provider = "MyVendor"
        member _.Send(scopeId, envelope) = async {
            let payload =
                match envelope.Notification with
                | TransactionalEmail emailPayload -> emailPayload
                | _ -> failwith "Wrong kind routed to email sink"
            // `GetSecret` returns `string option`.
            let! apiKey = secretStore.GetSecret("_platform", "MYVENDOR_API_KEY")
            // POST to vendor API; parse response
            let! response = httpClient.PostAsJsonAsync(settings.Endpoint, payload) |> Async.AwaitTask

            if response.IsSuccessStatusCode then
                return SinkResult.Delivered None
            else
                // 4xx is the caller's fault and will not improve on retry;
                // 5xx / timeout is what `TransientFailure` is for.
                return SinkResult.PermanentFailure $"{int response.StatusCode}"
        }
```

Wire:

```fsharp
ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withTransactionalSink (MyVendorEmailSink(settings, secretStore, httpClient) :> INotificationSink)
// ... the deployment's other ServerApp.with* calls ...
|> ServerApp.run
```

Rules:
- `Kind` discriminates routing — exactly one sink registered per `Kind`, and the compose-time uniqueness check keys on `SinkKind.toWireString`, so `Push PushVariant.WebPush` and `Push PushVariant.Fcm` register side by side without colliding. Duplicate registration of the same wire string is rejected at compose time.
- API keys / tokens come through `ISecretStore`. Rotation is the operator's lever; the sink reads per-call so rotated values flow through immediately. (The SMTP sink is the exception — see its section above.)
- Sinks should be idempotent across retries — the dispatcher retries on `TransientFailure` only; `PermanentFailure` goes straight to the audit trail, and `Skipped` emits nothing. Use vendor dedup keys (Message-ID, idempotency tokens, etc.).
- Author an `IHealthCheck` + `IConfigValidator` for self-registration.

For HTTP-shaped sinks, use BCL `HttpClient` rather than a vendor SDK where the API is permissive. This minimises the dep graph.

## Hardening checklist for production

- Redis channel for multi-instance pub/sub.
- Transactional sinks configured for every notification kind the app emits.
- API keys / tokens in `ISecretStore`, scoped to `_platform`.
- Per-team `notification_prefs` UI (custom; not SDK-built).
- Address book populated — users without contact details get `NotificationDeliveryFailed` events instead of silent drops.
- Bounce / unsubscribe handling (vendor-specific; deployments wire webhooks back).
- Health probes for each registered sink — `SmtpNotificationSinkHealth`, `SendGridNotificationSinkHealth`, etc.
- Audit-trail replication (`IAuditSink`) captures `NotificationSent` / `NotificationDeliveryFailed` events for compliance.

## Six-rule portability audit

`INotificationChannel` satisfies all six portability rules — Identity by value, async at every boundary, no callback/supervision hooks, stateless per invocation, structural per-scope topic isolation, minute-precision floor documented.

`INotificationSink` is sync-by-design *only at its two compose-time properties*, `Kind` and `Provider`; `Send` is async. The sink's `Kind` is identity-by-value (a `NotificationKind.SinkKind` DU case); no live framework handles cross the interface.

Conformance: `INotificationChannelContract` test pack covers per-scope topic isolation + delivery ordering within a scope. Drop-in alternatives validate against the same pack.

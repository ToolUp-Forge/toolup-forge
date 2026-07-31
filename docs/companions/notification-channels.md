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

```fsharp skip=fragment
let notificationChannel =
    RedisNotificationChannel.create
        { ConnectionString = "localhost:6379" }
        :> INotificationChannel

ServerApp.empty
|> ...
|> ServerApp.withNotificationChannel notificationChannel
|> ...
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

### Email — `ToolUp.NotificationChannels.Email.Smtp`

Generic SMTP via MailKit. Vendor-agnostic (works with Mailgun, Postmark, Amazon SES, in-house mail relay, etc.).

Setup:

```bash
TOOLUP_TRANSACTIONAL_EMAIL=smtp
TOOLUP_SMTP_HOST=smtp.example.com
TOOLUP_SMTP_PORT=587
TOOLUP_SMTP_USERNAME=...
TOOLUP_SMTP_FROM_EMAIL=noreply@example.com
TOOLUP_SMTP_FROM_NAME="My App"
```

Password stored in `ISecretStore` under `_platform`, key `SMTP_PASSWORD`.

```fsharp skip=fragment
open ToolUp.NotificationChannels.Email.Smtp

let sink = SmtpNotificationSink.create smtpSettings secretStore :> INotificationSink

ServerApp.empty
|> ...
|> ServerApp.withTransactionalSink sink
|> ...
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

```fsharp skip=fragment
open ToolUp.NotificationChannels.Email.SendGrid

let sink = SendGridNotificationSink.create sendGridSettings secretStore :> INotificationSink

ServerApp.empty
|> ...
|> ServerApp.withTransactionalSink sink
|> ...
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

```fsharp skip=fragment
open ToolUp.NotificationChannels.Sms.Twilio

let sink = TwilioNotificationSink.create twilioSettings secretStore :> INotificationSink

ServerApp.empty
|> ...
|> ServerApp.withTransactionalSink sink
|> ...
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

```fsharp skip=fragment
open ToolUp.NotificationChannels.Push.WebPush

let sink = WebPushNotificationSink.create webPushSettings secretStore :> INotificationSink

ServerApp.empty
|> ...
|> ServerApp.withTransactionalSink sink
|> ...
```

Browser side: register a Service Worker (`examples/sw.js` template ships with the companion). The Service Worker handles `push` events and shows OS notifications.

## How the dispatcher works

`TransactionalDispatcher` is a `BackgroundService` that drains a bounded `Channel<NotificationEnvelope>`. Per envelope:

1. Looks up the user's contact details via `INotificationAddressBook` (default: blob-backed `BlobBackedNotificationAddressBook` reads from `_platform/contacts/{scopeId}/{userId}.json`).
2. Resolves the vendor-neutral address (`EmailAddress` / `PhoneNumber` / `PushToken`).
3. Checks the per-team `_platform.notification_prefs` kill switches.
4. Routes by `Kind` (`Email` / `Sms` / `Push`) to the matching registered `INotificationSink`.
5. Calls `sink.Send envelope`.
6. Emits `NotificationSent` or `NotificationDeliveryFailed` audit event under `_platform.notifications`.

PII (email addresses, phone numbers, push tokens) NEVER crosses pub/sub topics — only `userId`s flow through the channel; addresses resolve at dispatch time via the address book.

Duplicate-`Kind` sink registration is rejected at compose time. If you want fallback (Postmark primary, SES secondary), wrap them in a `ChainedSink` composition you write yourself.

## Contact address book

`INotificationAddressBook` resolves `userId` → vendor-neutral addresses:

```fsharp
type INotificationAddressBook =
    abstract Resolve: scopeId: string -> userId: string -> Async<ContactAddresses>

and ContactAddresses = {
    EmailAddress: EmailAddress option
    PhoneNumber: PhoneNumber option
    PushTokens: PushToken list
}
```

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
    | SystemMessage of message: string * severity: Severity   // pub/sub
    | JobProgress of jobId: JobId * progress: float           // pub/sub
    | JobComplete of jobId: JobId * result: JobResult         // pub/sub
    | RefreshData of dataTypeId: string                       // pub/sub
    | CustomNotification of kind: string * payload: JsonValue // pub/sub
    | TransactionalEmail of EmailPayload                      // out-of-band via INotificationSink
    | TransactionalSms of SmsPayload                          // out-of-band via INotificationSink
    | MobilePush of PushPayload                               // out-of-band via INotificationSink
```

`DispatchingNotificationChannel` decorator routes by case:
- `SystemMessage` / `JobProgress` / `JobComplete` / `RefreshData` / `CustomNotification` → publish over `INotificationChannel` (pub/sub).
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

open ToolUp.Platform

type MyVendorEmailSink(settings: MyVendorSettings, secretStore: ISecretStore, httpClient: HttpClient) =
    interface INotificationSink with
        member _.Kind = NotificationKind.Email
        member _.Send(envelope) = async {
            let payload =
                match envelope.Notification with
                | TransactionalEmail emailPayload -> emailPayload
                | _ -> failwith "Wrong kind routed to email sink"
            let! apiKey = secretStore.GetSecret("_platform", "MYVENDOR_API_KEY")
            // POST to vendor API; parse response
            let! response = httpClient.PostAsJsonAsync(vendorUrl, ...) |> Async.AwaitTask
            response.EnsureSuccessStatusCode() |> ignore
            return Result.Ok ()
        }
```

Wire:

```fsharp skip=fragment
ServerApp.empty
|> ...
|> ServerApp.withTransactionalSink (MyVendorEmailSink(settings, secretStore, httpClient) :> INotificationSink)
|> ...
```

Rules:
- `Kind` discriminates routing — exactly one sink registered per `Kind`. Duplicate `Kind` registration is rejected at compose time.
- API keys / tokens come through `ISecretStore`. Rotation is the operator's lever; the sink reads per-call so rotated values flow through immediately.
- Sinks should be idempotent across retries — the dispatcher retries on `Result.Error`. Use vendor dedup keys (Message-ID, idempotency tokens, etc.).
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

`INotificationSink` is sync-by-design *only at the `Kind` discriminator method*; `Send` is async. The sink's compose-time `Kind` is identity-by-value (a `NotificationKind` DU case); no live framework handles cross the interface.

Conformance: `INotificationChannelContract` test pack covers per-scope topic isolation + delivery ordering within a scope. Drop-in alternatives validate against the same pack.

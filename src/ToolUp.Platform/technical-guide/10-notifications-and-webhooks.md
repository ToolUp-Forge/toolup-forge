# ToolUp.Platform Technical Guide — 10. Notifications & Webhooks

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> [← Prev: 9. Module Conventions, Data Flow & Build](09-module-conventions-data-flow-and-build.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 11. AI Integration & Closing Notes →](11-ai-integration-and-closing-notes.md)

---

## Real-time notification pipeline

`ServerApp.run` (and its `AIServerApp` / `RAGServerApp` wrappers) auto-injects a generic SSE push channel for any server code — SDK, companion, or app — that wants to notify connected clients without owning its own transport. The architecture has three collaborators:

1. **`INotificationChannel`** (`Shared/INotificationChannel.fs`) — the publisher/subscriber contract. The interface lives in Shared (not Server) so module projects that don't import `.Server.props` can still consume it from `ToolUp.Platform.dll` — e.g. `KnowledgeBase.Server` publishes `DataRefreshed("KnowledgeBase", scopeId)` after narrative ingestion. `Publish(scopeId, notification)` hands the notification to the transport, which broadcasts a `NotificationEnvelope` (Id: Guid, ScopeId, Notification DU, OccurredAt) to every subscriber registered for that scope. `Subscribe(scopeId, handler)` returns a `Guid` handle that `Unsubscribe` uses to detach. The `handler` is `NotificationEnvelope -> unit` (sync) — a documented Phase 9c Rule 2 exemption because per-item dispatchers stay sync by design; the method itself is `Async<_>`. Stateless (Phase 9c Rule 4) — receives all data via parameters.
2. **`SSEConnectionManager`** (`Server/SSEConnectionManager.fs`) — a scope-keyed `ConcurrentDictionary` of live HTTP responses. Resolved from DI by notifications, AI streaming, and future RAG progress feeds. Its single role is "hold the response open and route writes" — it does not know about notification semantics. Implements `IDisposable` so DI cleans up its 30 s keepalive timer at shutdown.
3. **`NotificationHandler.notificationHandler`** (`Server/NotificationHandler.fs`) — the Giraffe HTTP handler at `/api/notifications`. Per connection, it resolves scopeId via the shared `SseScopeResolution.resolve` (Phase 117 — same path as the AI SSE endpoint): the middleware-resolved principal always wins (a mismatching `?userId=` is ignored and audited), the query/header fallback (falling back to `"anonymous"`) applies only under `SseAuthMode = QueryParamFallback`, and under `CookieRequired` an unauthenticated connect is refused 401. It then registers with `SSEConnectionManager`, subscribes a callback on `INotificationChannel`, and runs a 15-second keepalive comment loop on its own response. On disconnect it unsubscribes and removes the connection.

### `SSEConnectionManager.Broadcast` — write awaiting and zombie eviction

`Broadcast(scopeId, bytes)` writes to every live connection registered for `scopeId`. The implementation does three things in one pass:

1. **Awaits each `WriteAsync` and `FlushAsync`.** Failed writes (closed tab, network drop, proxy hung up) raise — without awaiting, a fire-and-forget write would silently drop the bytes and leave the dead connection in the registry until the next inbound publish happened to detect it.
2. **Eagerly evicts failed connections.** Connections whose write threw or whose `CancellationToken.IsCancellationRequested` is `true` are collected into a per-broadcast `dead` list and removed from the dictionary in one final `AddOrUpdate` pass. The eviction is structural — no zombie can outlive a single publish round trip.
3. **Runs a 30-second keepalive timer.** A `System.Threading.Timer` walks every active scope every 30 s and broadcasts `SSE.keepaliveBytes` (`: keepalive\n\n` — an SSE comment, ignored by clients). Two purposes: (a) prevents TLS / reverse-proxy idle timeouts (typical 60 s default) from dropping the connection, and (b) gives the server a write attempt every 30 s so closed clients surface as failed writes and get evicted even when no inbound publish is happening. Without this, a closed tab can linger in the registry for hours waiting for the next `Publish` call.

The synchronous `Broadcast` method is a shim over an internal `task { ... }` — it does `(broadcastAsync ...).GetAwaiter().GetResult()` because the historic callers (NotificationHandler, AI's `SSEHandler`) treat the method as a one-shot fire-and-forget call. Awaiting inside `broadcastAsync` is the correct semantics; the synchronous shim makes the eviction visible to the caller before returning.

### Serialization rule (non-negotiable)

Notifications cross the manual server→Fable boundary — they are not ToolUp.Remoting. `NotificationHandler` serialises every envelope with `FableJsonConverter` (under the preserved `Fable.Remoting.Json` namespace; ships inside `ToolUp.Platform.Server`). Do **not** replace this with `Newtonsoft.Json.Converters.DiscriminatedUnionConverter` — the output shape `{"Case":"X","Fields":[...]}` is not parseable by `Fable.SimpleJson` on the client. Do **not** add a `CamelCasePropertyNamesContractResolver`. This matches the existing SSE rule documented in `ToolUp.AI/TECHNICAL_GUIDE.md`; AI inherited the rule, notifications reaffirm it.

### Named events, not generic `data:`

Each `NotificationKind` (`SystemMessage`, `JobProgress`, `JobComplete`, `RefreshData`, `CustomNotification`) is emitted as an SSE named event — `event: JobProgress\ndata: {...}\n\n`. The client router (`Client/NotificationClient.fs`) installs one `addEventListener(name, handler)` per kind. This lets subscribers route at the kind level without parsing the envelope just to discriminate, and lets browsers' devtools group events usefully.

### Reserved notification kinds (cross-scope wire-format contracts)

Most notification kinds are scope-private — a `JobProgress` envelope on scope A is invisible to subscribers on scope B by structural transport isolation (per-scope topic). A small set of kinds intentionally crosses the per-scope topic boundary because deeply-coupled SDK consumers subscribe to them by literal name, and the literal therefore becomes a published wire-format contract that callers and consumers must agree on:

- **`KnowledgeBase.IngestionStatus`** — published by `ToolUp.RAG`'s ingestion pipeline; subscribed to by the AI side panel (`AIAssistantUI`) so users see "indexing your file…" inline with their chat. AI cannot depend on KB (AI is more foundational), so the literal is duplicated rather than shared via a constant. Defined as `[<Literal>] IngestionStatusNotificationKey` in `ToolUp.KnowledgeBase`'s shared module; hardcoded as a string in `AIAssistantUI.fs`. Any external KB replacement (Phase 1e `KnowledgeBaseMode = External`) either matches this literal or accepts that the AI panel will not surface its progress.
- **`MembershipChanged`** — published by `TeamStore` after every successful membership write (`AddMember`, `RemoveMember`, `ChangeMemberRole`, `SetActiveTeam`) on the reserved `_platform` topic (`NotificationKind.PlatformReservedScope`). Subscribed to by `TeamScopeResolver` (evicts the affected user's `IMemoryCache` entry on `Removed` / `ActiveTeamSet`; ignores `Added` / `RoleChanged` since those don't change *which* team is active), and by `NotificationHandler`'s per-connection bridge subscription that filters `AffectedUserId = scopeId` so each user's `EventSource` only delivers events that name them. The client `NotificationClient` registers an `addEventListener("MembershipChanged", …)` and the shell routes the event into the existing `TeamSwitched` reset path (`Removed` → `TeamSwitched None` if the removed team is the active one; `ActiveTeamSet` → `TeamSwitched (Some newTeamId)` if the new team differs from the active one). Crosses the per-scope boundary because invalidation must reach caches keyed on the *removed* user's scope after they no longer belong to it. Payload: `{ TeamId; AffectedUserId; ChangeKind: Added | Removed | RoleChanged | ActiveTeamSet; PublishedAt }`. `TeamScopeResolver.InvalidateUser` is preserved as a thin shim that publishes `ActiveTeamSet` so legacy callers and the tests at `StorageScopeResolverTests.fs` continue to work without manual invalidation. `ConfigHandler` and `FeatureFlagHandler` do not subscribe today — neither maintains a per-scope cache; revisit if either grows one.

When adding a future reserved kind: document it here, declare the literal in the publishing companion's shared module, and accept that any consumer in a less-foundational position must duplicate the string rather than introduce a back-reference. The principle is the wire format is the contract — not the F# constant.

### Per-connection subscription, not per-scope fan-out

Each `EventSource` gets its own subscription + its own callback that writes only to its own response. For N tabs in the same scope, the in-memory channel calls N callbacks per publish — O(N) but trivially cheap at expected fanouts. A distributed implementation (Redis pub/sub, Kafka, etc.) is free to shard differently: the interface only promises "every subscriber sees the envelope", not "one callback per scope". This avoids the N×N duplicate-write bug a `Broadcast(scope)` API would invite.

### Identity is by value

Subscription handles are `Guid`. No `IDisposable` handle, no live reference to an in-memory callback. This satisfies Phase 9c Rule 1 (identity by value — no framework-specific runtime handles in the interface). `Unsubscribe(scopeId, Guid)` works identically across an in-memory channel and a future Redis / Orleans implementation.

### Writing a new `INotificationChannel` companion

The transport is swappable — the default `InMemoryNotificationChannel` works in a single process, and a distributed deployment swaps in Redis pub/sub, NATS, Orleans streams, or a SignalR backplane without any change to `compose` / `SSEConnectionManager` / `NotificationHandler`. The shipped Redis companion at `src/NotificationChannels/Redis/` is the reference pattern:

**File layout.** A `.fsproj` using `Microsoft.NET.Sdk` with `<None Include="Foo.fs" />` + `<None Include="paket.references" />` (no `<Compile>` — the companion dll carries no code), a `.Server.props` that injects the source via the existing `_ToolUpPlatformServerSources` hook pattern, a `paket.references` listing the transport's client library, and a `<ProjectReference>` in the consuming server `.fsproj` that pulls the paket dependency into the runtime graph. This mirrors `src/Storage/*`, `src/AIProviders/*`, and `src/EmbeddingProviders/*` — same pattern, no new concept to learn.

**Implementation contract.** Implement the three `INotificationChannel` members. `Publish` returns when the transport accepts the envelope (the contract does not require synchronous fan-out — in-process channels may complete callbacks before returning, but cross-process transports should return immediately after `PUBLISH` is sent). `Subscribe` returns a fresh `Guid` that `Unsubscribe` uses to detach, and the handle must be opaque — never an `IActorRef`, `IGrainReference`, or any live transport handle. Handler exceptions must be caught and logged, never bubbled — a slow or broken subscriber for one scope must not poison sibling scopes' dispatch.

**Scope isolation is structural, not a filter.** The Redis companion uses one Redis channel per `scopeId` (`toolup:notifications:{scopeId}`) — subscribers for scope A listen on a different Redis channel from scope B and cannot see B's publishes even if the post-hoc filter is bugged. Any new companion should enforce scope isolation at the transport layer in the same way (dedicated topic / subject / stream per `scopeId`), not via subscriber-side filtering on a shared topic. GP 4 (team isolation) is enforced by the transport, not by application code.

**Serialization shape.** Serialise envelopes with `Fable.Remoting.Json.FableJsonConverter` for the same reason as the SSE path — this is future-proofing, so a distributed deployment can replay an envelope straight from the transport into an SSE stream without a re-serialisation hop. `Notification` is a DU; without the converter, `Fable.SimpleJson` on future client-side replay paths could not deserialise the `{"Case":"X","Fields":[...]}` shape Newtonsoft produces by default.

**Wiring.** Apps resolve a channel from environment variables and pass it via `ServerApp.withNotifications (Some channel)` on their `ServerApp`/`AIServerApp`/`RAGServerApp` pipeline; that forwards it into the underlying `compose` / `composeWithAI` / `composeWithRAG` call. The reference resolver in `src/ToolUpApp-Server/Server.fs` keys on `TOOLUP_NOTIFICATION_CHANNEL` with a fail-soft policy — a selected-but-misconfigured backend falls back to the in-process default with a warning, matching the `TOOLUP_BLOB_STORAGE` pattern. `None` keeps the in-process default; core `compose` constructs `InMemoryNotificationChannel` itself so the `logger` capture stays in core.

**Contract test binding.** Bind your companion to the shared `INotificationChannelContract` pack (`src/ToolUp.Platform.Tests/Contracts/INotificationChannelContract.fs`) — the same pack the in-memory default passes. When external infrastructure is required, env-gate the binding with `ptestCase "skipped — ENV_VAR not set"` so CI shows "skipped" rather than silent green; `RedisNotificationChannelTests.fs` demonstrates the pattern. Every new companion must pass the pack unchanged — if it doesn't, the interface is the bug.

### Phase 9c portability rule audit — worked example

The Redis companion is also the reference audit. The six rules from Guiding Principle 12, checked against both implementations:

- **Rule 1 (identity by value).** `NotificationSubscriptionId = Guid` across both implementations. The Redis companion's internal `ConcurrentDictionary<Guid, (string, handler, ChannelMessageQueue)>` keeps the `ChannelMessageQueue` private — callers never see it. A caller that serialises its subscription id through a database and replays it into a different channel instance gets an idempotent no-op from `Unsubscribe`, not a crash. ✓
- **Rule 2 (async at every boundary).** Every method is `Async<T>` on both implementations. `Publish` is `Async<unit>`, `Subscribe` is `Async<Guid>`, `Unsubscribe` is `Async<unit>`. No synchronous escape hatch. ✓
- **Rule 3 (retry as data).** Notifications are fire-and-forget by contract — `Publish` returns on transport acceptance, not guaranteed delivery. There is no callback-style `OnFailure` parameter and no supervision strategy in the interface; producers that need delivery guarantees layer them above the channel (e.g., persist the envelope to `IEventStore` first, then publish). Both implementations honour this by not taking retry-policy-as-callback parameters. ✓
- **Rule 4 (stateless handlers).** `Subscribe(scopeId, handler: NotificationEnvelope -> unit)` — the handler receives all state through the envelope parameter; no in-memory state is required between invocations. The Redis companion's `ChannelMessageQueue.OnMessage` dispatch runs on StackExchange.Redis-owned workers that can restart at any time; handlers that survived this didn't leak cross-invocation state. ✓
- **Rule 5 (no cross-shard ordering promises).** The interface documents that ordering is guaranteed within a single `scopeId` only — there is no claim that cross-scope `Publish` calls arrive in submission order. The Redis companion inherits Redis pub/sub's per-channel ordering but makes no cross-channel promise. ✓
- **Rule 6 (precision at the lower bound).** `NotificationEnvelope.OccurredAt: DateTime` is populated at publish time with no sub-second precision guarantee. The contract already documents notifications as "near-real-time, not sub-second" — both implementations honour this. ✓

All six rules passed on the current interface. The Redis audit produced exactly one documentation tightening (`Publish` returns on transport acceptance, not guaranteed delivery — added to the interface doc comment) and zero interface retrofits. This is the point of running the audit before building a distributed companion, not after.

### Disabling notifications (Phase 58)

Four `Notifications` modes plus the `NotificationsAuto` default — pick the one that matches the deployment's transport intent rather than reaching for the shape that produces "no errors":

| Mode | When to use | Server-side effect | Client-side effect |
|---|---|---|---|
| `NotificationsAuto` (default) | You want the SDK to decide — lightweight apps get `NoNotifications`, anything publishing notifications (jobs, MultiTeam, AI/RAG consumers) gets `InMemoryNotifications`. | `NotificationMode.resolve` flips to `InMemoryNotifications` whenever a publisher is active, otherwise `NoNotifications`. | EventSource is created on first `subscribe`; if the server has no route mounted, the defensive 404 fallback closes out for the session. |
| `NoNotifications` | You want the auto-default behaviour pinned to "off" even though something would otherwise flip it on (e.g. you use webhooks for fan-out and don't want a second transport). | No `INotificationChannel`, no `/api/notifications` route. | EventSource is created, 404s, the defensive fallback closes the connection. One 404 per tab, then quiet. |
| `NoNotificationsExplicit` | You want to suppress the SSE transport entirely with no 404 detour. Pair with `__TOOLUP_NOTIFICATIONS_DISABLED__ = true` in the consumer's Vite config. Right shape for serverless / public-utility deployments where SSE is fundamentally inappropriate (Azure Functions Consumption has a 230s execution cap; AWS Lambda has no native SSE; Cloudflare Workers' SSE story is browser-vendor-flagged). | Identical to `NoNotifications` server-side — no channel, no route. | `BundleConstants.notificationsDisabledExplicitly` is `true`, so `NotificationClient.ensureConnected` early-returns without opening EventSource. Zero `/api/notifications` requests in DevTools Network. |
| `InMemoryNotifications` | Single-process Kestrel deployment with jobs, MultiTeam, or AI/RAG. The default lightweight target. | `InMemoryNotificationChannel` registered, `/api/notifications` mounted. | EventSource opens, receives envelopes from in-process publishers. |
| `RedisNotifications "<connection-string>"` | Multi-instance deployment. Required for team-scoped fleets — see "Phase 9c portability rule audit" above. | `RedisNotificationChannel` from the `src/NotificationChannels/Redis/` companion. | EventSource opens; envelopes fan out across replicas via Redis pub/sub. |

**Why `NoNotificationsExplicit` exists.** With `Notifications = NoNotifications` (the lightweight default for apps without jobs / Team / AI / RAG consumers), the server-side route is unmounted but the client-side `NotificationClient` still opens EventSource against `/api/notifications` on first `subscribe`. The browser receives a 404 and (depending on the version) may retry-loop until the defensive fallback in `onError` catches it. `NoNotificationsExplicit` paired with the Vite define short-circuits that loop at the source: the client never tries.

**Wiring the Vite define.** `vite.config.mts`:

```ts
import { defineConfig } from "vite";

export default defineConfig({
  define: {
    __TOOLUP_NOTIFICATIONS_DISABLED__: JSON.stringify(true),
    // ... __TOOLUP_MODULE__, __AG_GRID_LICENSE__, __CLERK_PUBLISHABLE_KEY__
  },
});
```

When the define isn't wired, `BundleConstants.notificationsDisabledExplicitly` reads as `false` and the client behaves as it does today — the existing 404 fallback still catches the silent default, so consumers who set `ServerConfig.Notifications = NoNotificationsExplicit` but forget the Vite define still avoid the retry loop, they just pay one 404 per tab to get there.

**Upgrade path for existing deployments.** A deployment that today pins `Notifications = InMemoryNotifications` purely to make the route exist (and so plug the client's 404 loop) — and otherwise has no in-process publisher — can flip to `NoNotificationsExplicit` and add the Vite define in the same change. The migration doc at `docs/migrations/58-notifications-explicit-off.md` walks the consumer-side diff.

### SSE transport stays in core; only `INotificationChannel` is pluggable

`SSEConnectionManager` and `NotificationHandler` stay in core regardless of the channel backend. The Redis companion does not bring its own HTTP handler — it publishes into Redis, and the in-process subscribers running inside each app instance receive and write to their local `SSEConnectionManager`'s connections. The decoupling lets a fleet of app instances share one Redis pub/sub cluster while each keeping its own per-tab `EventSource` connections. This is intentional: the interface separates "fan-out across the backend" (pluggable) from "hold a response open and route writes" (core HTTP-framework concern).

### Companion integration

AI and RAG companions no longer own `SSEConnectionManager`. They resolve it from DI inside per-request handlers:

```fsharp
let resolveManager (ctx: HttpContext) =
    ctx.RequestServices.GetService(typeof<SSEConnectionManager>) :?> SSEConnectionManager
```

AI's `/api/ai/events` still exists — its wire format is AI-specific (streaming tokens) and kept separate from `/api/notifications`. What changed is ownership: the transport plumbing is now core.

### Client subscription model

`NotificationClient.subscribe (dispatch: NotificationEnvelope -> unit)` opens one `EventSource` per subscriber and returns a dispose thunk. The built-in `ToastCentre` subscribes on mount, filters to `SystemMessage`, and unsubscribes on unmount. Apps that want custom notification handling — job-completion banners, refresh prompts, custom UI — subscribe independently to the same endpoint. Multiple subscribers mean multiple `EventSource` connections; browsers permit ~6 per origin, which is ample for the expected 1–2 consumers.

### Why ToastCentre state is local React state, not Elmish

Toast display is pure transient UI — the persisted source of truth already lives on the server, so pushing it through the Elmish update loop would mean model churn on every notification without any correctness benefit. `ToastCentre` uses `React.useState<ActiveToast list>` and `React.useEffectOnce` to subscribe. This matches the existing "text inputs and transient UI state" convention (`UIToolkit.Forms.Input.currency`, `AIAssistantUI.MessageInput`, `ConversationPanel`).

### Fable-compatibility note

`DateTime.MaxValue` is Fable-compatible but `TimeSpan.MaxValue` is not (it raises `Cannot compile ILFieldGet(DeclaredType TimeSpan, MaxValue)` at Fable compile time). The `ToastCentre` `Error`-level "never auto-dismiss" sentinel is stored as an absolute `DateTime.MaxValue` deadline, not as `now + TimeSpan.MaxValue`. Any future code that needs a "forever" duration on the client must use the same pattern.

## Transactional notifications (Phase 6f)

Phase 6f extends `INotificationChannel` with **out-of-band transactional delivery** — email, SMS, mobile push — for events whose audience isn't a live SSE consumer. A `JobCompleted` should email the requester even when no tab is open; a `MemberRoleChanged` triggered from outside the office should SMS the on-call. Three new `Notification` cases (`TransactionalEmail`, `TransactionalSms`, `MobilePush`) ride the same channel for portability with the Phase 6e Redis backend, but the wrapping `DispatchingNotificationChannel` decorator routes them to a queue instead of the wire.

### The dispatcher / sink split

Two collaborators:

1. **`DispatchingNotificationChannel`** (`Server/TransactionalDispatcher.fs`) — `INotificationChannel` decorator. Forwards every non-transactional `Publish` to the inner channel (in-process or Redis). Intercepts transactional kinds, stamps a fresh `NotificationEnvelope`, and enqueues to `TransactionalDispatcher`. **Bypasses the inner transport entirely** for transactional kinds — PII never crosses pub/sub topics; SSE subscribers can't receive the kind even by accident; envelope identity is single-stamped (matches the audit trail).

2. **`TransactionalDispatcher`** — `BackgroundService` draining a bounded `Channel<DispatchTask>` (capacity 256, `DropWrite` on overflow with `Warn` log; same idiom as `WebhookDispatcher`). For each task: pre-flight prefs check, sink dispatch, retry loop, audit emission. Mirrors `WebhookDispatcher.runDelivery` in retry shape (`TransactionalRetryPolicy.defaults` = 3 attempts, 30s initial backoff, 10min cap).

The decorator + dispatcher pair is constructed only when at least one `INotificationSink` is registered. `compose` validates duplicate-`Kind` registration at construction time so a misconfigured deployment fails to start rather than silently dropping half its delivery.

### `INotificationSink`

Adapter contract — one implementation per vendor (`SmtpNotificationSink`, `SendGridNotificationSink`, `TwilioNotificationSink`, `WebPushNotificationSink`):

```fsharp
[<RequireQualifiedAccess>]
type SinkResult =
    | Delivered of vendorMessageId: string option
    | Skipped of reason: string
    | TransientFailure of error: string
    | PermanentFailure of error: string

type INotificationSink =
    abstract Kind: string      // "Email" / "Sms" / "Push"
    abstract Provider: string  // "Smtp" / "SendGrid" / "Twilio" / "WebPush" / ...
    abstract Send: scopeId: string * envelope: NotificationEnvelope -> Async<SinkResult>
```

`SinkResult` carries `[<RequireQualifiedAccess>]` because `JobResult` (Phase 9b) uses `TransientFailure` / `PermanentFailure` case names too — F# type inference would otherwise resolve to whichever DU was declared most recently and break `JobScheduler.runDelivery`. Sinks classify on the vendor's response: HTTP 5xx and network errors are `TransientFailure` (retried per policy); 4xx and contract violations are `PermanentFailure` (immediate audit, give up); 410 Gone on push tokens means the subscription expired (`PermanentFailure` so the address book can evict).

Sinks **do NOT emit audit events themselves** — the dispatcher reads the `SinkResult` and emits `NotificationSent` / `NotificationDeliveryFailed`. Centralising audit at the dispatcher avoids per-sink bookkeeping drift and keeps sinks minimal — the smallest viable sink is one method.

### `INotificationAddressBook`

Phase 6f is built on the principle that **PII never crosses the channel wire**. Envelopes carry `RecipientUserIds: string list` only — sinks resolve userIds to vendor-neutral `EmailAddress` / `PhoneNumber` / `PushToken` at dispatch time, hand them straight to the upstream vendor, and never persist the resolved values anywhere (audit trail records userIds only).

```fsharp
type INotificationAddressBook =
    abstract ResolveEmail: userId: string * scopeId: string -> Async<EmailAddress option>
    abstract ResolvePhone: userId: string * scopeId: string -> Async<PhoneNumber option>
    abstract ResolvePushTokens: userId: string * scopeId: string -> Async<PushToken list>
```

Two SDK defaults ship: `NoOpNotificationAddressBook` (returns None / [] always — safe for deployments without a directory; sinks `Skipped`-per-recipient) and `BlobBackedNotificationAddressBook` (reads `_platform/contacts/{scopeId}/{userId}.json` JSON via `IBlobStorage`). The `UserContact` record is in the shared layer so future Fable admin UIs read / write the same shape. The blob-backed default registers in DI by default; deployments substitute LDAP / Okta / Azure AD impls by overriding the singleton post-`compose`.

**Scope-isolated lookups.** `(userId, scopeId)` is the lookup key, not just `userId`. A user belonging to two teams may have different push tokens registered per team; cross-team resolution returning a different team's data is a team-isolation breach (GP 4). The blob layout enforces this structurally (one folder per scope).

### Per-team prefs (`_platform.notification_prefs`)

The dispatcher consults `IConfigStore.GetRaw` for each envelope before invoking any sink:

| Field | Default | Meaning |
|---|---|---|
| `email.enabled` | `false` | Team-wide kill switch for transactional email |
| `email.fromAddress` | `""` | Override for the deployment-default `From:` address |
| `sms.enabled` | `false` | SMS kill switch (avoids Twilio charges from accidental sends) |
| `push.enabled` | `false` | Push kill switch |

A `false` toggle short-circuits the dispatcher to `Skipped "team_opted_out"`: no audit, no retry. The team has not authorised outbound delivery, so we treat the publish as if it never happened. The schema is auto-merged into `ServerConfig.ModuleConfigs` by `compose` whenever any sink is registered, so admins see the prefs tab automatically. **Per-user override** of these defaults is a deferred follow-up — it requires a small `IConfigStore` extension to read user-scope without resolving via `IStorageScopeResolver`.

### Audit emission

Two new `AuditEvent` cases (Phase 6f step b):

- `NotificationSent` — emitted on `SinkResult.Delivered`. Carries `NotificationKind` (`"Email"` / etc.), `Provider` label (`"Smtp"` / `"SendGrid"` / etc.), `RecipientUserIds`, optional `VendorMessageId` (SendGrid `X-Message-Id`, SMTP `Message-ID`, Twilio `MessageSid`), optional `CorrelationId`.
- `NotificationDeliveryFailed` — emitted on first-attempt `PermanentFailure` or retry-exhausted `TransientFailure`. Carries the same metadata plus `Error` string and `Attempts` count.

Both flow through `IAuditLog.Record` under `SourceModule = "_platform.notifications"`, persisted to `IEventStore` via the existing `EventStoreAuditLog` decorator. `Skipped` (configuration no-op) emits no audit. Operators correlate vendor logs with the audit trail via `VendorMessageId` / `CorrelationId`.

The dispatcher uses a reserved `UserId = "system"` for system-driven publishes (job lifecycle is the bulk source). A future overload could carry an actor id on the envelope.

### Vendor companions

| Family | Companion | Default? | Vendor cost? | Notes |
|---|---|---|---|---|
| Email | `Smtp` | yes | no | MailKit, MIT-licensed; works against MailHog / customer mail relay / SES SMTP. `TemplatedEmail` returns `PermanentFailure`. |
| Email | `SendGrid` | no | yes | Pure HTTP REST against api.sendgrid.com; supports `dynamic_template_data`. API key from `ISecretStore`. |
| Email | `Postmark` | no | yes | Directory reserved with README only — implementation deferred. |
| SMS | `Twilio` | no | yes | One POST per recipient (no native fan-out); Basic auth, token from `ISecretStore`. |
| Push | `WebPush` | no | no | Uses the `WebPush` NuGet package (RFC 8030 + VAPID). VAPID private key in `ISecretStore`; public key + subject in env. Includes `examples/sw.js` reference service worker. |

Every companion follows the established pattern: `<Vendor>NotificationSink.fs` + `.Server.props` + `.fsproj` + `paket.references` under `src/NotificationChannels/<Family>/<Vendor>/`. The reference app's `ToolupApp-Server.fsproj` imports all four `.Server.props` files plus `<ProjectReference>`s; deployments not using a vendor strip the import + project reference to remove the corresponding client SDK from the bundle.

### Six-rule portability audit (Phase 9c)

| Rule | Surface | Verdict |
|---|---|---|
| 1. Identity by value | `EmailAddress.Address: string`, `PhoneNumber.E164: string`, `PushToken: { string * string }`, `INotificationSink.Kind: string`, `vendorMessageId: string option` — no vendor handles in interface signatures | ✓ |
| 2. Async at every boundary | `INotificationAddressBook.Resolve*` and `INotificationSink.Send` both return `Async<_>` | ✓ |
| 3. Retry / supervision as data | `TransactionalRetryPolicy` is a record, no `OnFailure: exn -> unit` callbacks | ✓ |
| 4. Stateless handlers | `Send` derives outcome from `(scopeId, envelope)` plus injected dependencies; no in-memory state across calls | ✓ |
| 5. No cross-shard ordering | Documented: "delivery order across recipients is not guaranteed; per-recipient at-most-once" | ✓ |
| 6. Precision lower bound | Documented: "best-effort, vendor-queued; no upper-bound delivery time" | ✓ |

### Single-instance limitation

The dispatcher's bounded queue and retry timer are in-process. A multi-silo deployment running with a Redis `INotificationChannel` will have every silo see its own publishes; only the publishing silo dispatches transactional envelopes (because we bypass Redis for those kinds). At-most-once best-effort delivery; a publishing silo crashing between enqueue and dispatch loses the envelope. Documented contract; distributed retry / leasing is a Phase 9c follow-up — same status as the Phase 6d webhook dispatcher.

## Outbound webhooks

`ServerApp.run` (and its `AIServerApp` / `RAGServerApp` wrappers) auto-injects an outbound webhook pipeline that lets a deployment push events to third-party systems (Slack, PagerDuty, Zapier, customer-owned ingestion) when state changes — the inverse of the SSE notification pipeline above, which is for browser tabs. The pipeline is opt-in per scope: admins create a `WebhookSubscription` through the built-in `_sdk.WebhookAdmin` UI; the dispatcher fans out matching events to the subscriber's URL with an HMAC-SHA256 signature.

### Architecture

The pipeline has four collaborators:

1. **`HookedEventStore`** (`Server/HookedEventStore.fs`) — a decorator wrapped around the configured `IEventStore`. After every successful `Append`, it fires a single post-write hook with the event. `compose` installs the decorator unconditionally and registers the dispatcher's enqueue function as the hook. Modules that emit events through `IEventStore.Append` get fan-out for free — no per-module wiring.
2. **`IWebhookRegistry`** (`Server/IWebhookRegistry.fs`) — blob-backed CRUD for `WebhookSubscription` records. Subscriptions persist to `_platform/webhooks/{scopeId}/subscriptions/{subscriptionId:N}.json`; delivery log entries to `_platform/webhooks/{scopeId}/deliveries/{subscriptionId:N}/{yyyy-MM-ddTHH-mm-ss-fffffffZ}-{deliveryId:N}.json`. The ISO-8601 timestamp prefix on delivery filenames means `IBlobStorage.List` returns them in chronological order without server-side sorting.
3. **`WebhookDispatcher`** (`Server/WebhookDispatcher.fs`) — a `BackgroundService` consuming a bounded `System.Threading.Channels.Channel<DispatchTask>` (capacity 1024, drop-on-full with a logged warning). For each dequeued event, it loads the scope's active subscriptions, filters by `EventTypes`, and runs `deliverWithRetry` per match. Retry is in-process: `WebhookRetryPolicy { MaxAttempts = 5; InitialBackoff = 30s; MaxBackoff = 30min; DisableAfterConsecutiveFailures = 5 }`, exponential backoff with `min(InitialBackoff * 2^(attempt-1), MaxBackoff)`. Dead-letter on exhaustion records a `WebhookDeliveryFailed` audit event; `DisableAfterConsecutiveFailures` consecutive dead-letters auto-flip `Status = Disabled` and emit `WebhookSubscriptionAutoDisabled`.
4. **`WebhookAdminUI`** (`Client/WebhookAdminUI.fs`) — auto-injected admin module under reserved Id `_sdk.WebhookAdmin`, listed in the sidebar's Admin group for every non-`Anonymous` mode. Provides list / create / pause / resume / delete / test-fire and a per-subscription delivery log viewer.

### Two outbound JSON shapes

Outbound delivery payloads to third-party receivers use plain `System.Text.Json` with `JsonNamingPolicy.CamelCase` — receivers (Slack incoming-webhook handlers, Zapier triggers, customer ingestion) expect camelCase, not Newtonsoft's `Case`/`Fields` DU shape. Persisted audit-event payloads and the delivery log use `Fable.Remoting.Json.FableJsonConverter` because the admin UI deserialises them via Fable.Remoting / SimpleJson. Two converters, one per audience — do not unify them.

### HMAC-SHA256 signed delivery

Every POST carries an `X-ToolUp-Signature` header of the form `sha256=<lowercase-hex>` over the raw request body, keyed by the subscription's `Secret`. Receivers must compute the same HMAC over the bytes they read and compare constant-time. Worked examples for the three common server runtimes:

**Python (Flask / FastAPI):**

```python
import hmac
import hashlib

def verify_toolup_signature(body: bytes, header: str, secret: str) -> bool:
    if not header or not header.startswith("sha256="):
        return False
    expected = hmac.new(secret.encode("utf-8"), body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, header[len("sha256="):])
```

**Node.js (Express):**

```javascript
const crypto = require("crypto");

function verifyToolupSignature(body, header, secret) {
  if (!header || !header.startsWith("sha256=")) return false;
  const expected = crypto.createHmac("sha256", secret).update(body).digest("hex");
  const provided = header.slice("sha256=".length);
  const a = Buffer.from(expected, "hex");
  const b = Buffer.from(provided, "hex");
  return a.length === b.length && crypto.timingSafeEqual(a, b);
}
```

The Express handler must read the raw body before JSON parsing — `bodyParser.raw({ type: "application/json" })` or `express.raw({ type: "application/json" })` — otherwise the bytes the receiver hashes won't match the bytes the sender hashed.

**.NET (ASP.NET Core minimal API):**

```csharp
using System.Security.Cryptography;
using System.Text;

static bool VerifyToolupSignature(byte[] body, string? header, string secret)
{
    if (string.IsNullOrEmpty(header) || !header.StartsWith("sha256=")) return false;
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    var expected = Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    var provided = header["sha256=".Length..];
    return CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(expected),
        Encoding.ASCII.GetBytes(provided));
}
```

The constant-time comparison (`hmac.compare_digest`, `crypto.timingSafeEqual`, `CryptographicOperations.FixedTimeEquals`) is mandatory — a `==` comparison leaks the prefix length of the secret through timing and is the canonical webhook-verification mistake. Reject any request that fails verification with HTTP 401 before parsing the JSON body.

### Reserved namespaces

The webhook pipeline owns three reserved names:

- **Blob storage**: `_platform/webhooks/{scopeId}/subscriptions/` and `_platform/webhooks/{scopeId}/deliveries/{subscriptionId:N}/` under the `_platform` container. No module-side persistence is permitted under `_platform/webhooks/` — it is the registry's exclusive prefix.
- **Sidebar Id**: `_sdk.WebhookAdmin`. The reserved-Id convention mirrors `_sdk.Teams` / `_sdk.TeamConfig` — auto-injected, gated by mode. A module that registers an `_sdk.*` Id will collide with the auto-injected SDK module and break the shell.
- **Audit-event source**: `_platform.webhooks`. Five audit events flow through `IEventStore` from the dispatcher with `SourceModule = "_platform.webhooks"`: `WebhookSubscriptionCreated`, `WebhookSubscriptionStatusChanged`, `WebhookSubscriptionDeleted`, `WebhookDeliveryFailed` (terminal — emitted once per dead-lettered delivery, not once per failed attempt), `WebhookSubscriptionAutoDisabled`. These flow through `HookedEventStore` like any other event, which means a webhook subscription on `WebhookDeliveryFailed` will receive its own dead-letter notifications. Recursive subscriptions (a `_platform.webhooks` event-type subscriber whose target is itself failing) self-throttle through the `DisableAfterConsecutiveFailures` auto-disable threshold; tightening this is a Phase 9c follow-up if it becomes an operational issue.

### Single-instance dispatcher limitation

The dispatcher's `Channel<DispatchTask>` and retry timeline are in-process. Horizontal scale-out (two app instances behind a load balancer) currently exhibits two failure modes:

1. **Duplicate delivery on shared events.** Both instances run a `HookedEventStore`, both fire the post-write hook for events they wrote — but in a shared deployment the `IEventStore` typically lives in shared storage (blob, Redis), so events written by instance A are not seen by instance B's hook. Single delivery — but only because the hook fires per-writer, not per-event. The footgun is in the other direction: if a deployment moves to a `IEventStore` with cross-instance replay (e.g., a Kafka-backed implementation), the post-write hook will fire on every replicate that observes the event, multiplying deliveries by the replication factor.
2. **Retry timeline lost on restart.** Pending retries live in memory. An instance restart drops every queued retry; the dead-letter audit event is not emitted because the dispatcher never reaches `MaxAttempts`. Persistent retries require persisting the retry timeline to blob (or pushing the retry timeline onto a durable queue) and restoring it on startup.

Both are Phase 9c follow-ups: the registry interface is already async (rule 2), `WebhookRetryPolicy` is already data not callbacks (rule 3), and `SubscriptionId` is `Guid` not a runtime handle (rule 1). What's missing is a distributed scheduler for the retry timeline and a leader-election or competing-consumer pattern for the post-write hook so only one instance dispatches each event. Until that lands, deployments must run a single dispatcher instance — a typical pattern is to scale the API tier horizontally but pin the `WebhookDispatcher` background service to one replica via deployment configuration (Kubernetes leader-elected `Lease`, App Service WEBSITE_INSTANCE_ID gate, etc.).

### Test-fire bypasses retry

`IWebhookApi.TestFire` synthesises a `WebhookTest` event and runs a single `deliverOnce` attempt — no retries, no dead-letter, no audit event. The result returns to the admin UI directly. This is intentional: an admin clicking "Test fire" wants immediate feedback on whether the URL responds, not a 30-minute exponential-backoff sequence behind a reload-the-page-to-see-the-result UX.


---

> [← Prev: 9. Module Conventions, Data Flow & Build](09-module-conventions-data-flow-and-build.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 11. AI Integration & Closing Notes →](11-ai-integration-and-closing-notes.md)

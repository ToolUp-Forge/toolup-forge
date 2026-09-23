# Google Calendar bridge

Phase 830 `ICalendarBridge` companion. Mirrors `ToolUp.Scheduling` bookings into Google
Calendar and pulls edits made there back, over the Calendar v3 REST API. OAuth rides the
platform's own Authorization Code and token-refresh substrates; change notifications arrive on
a Google watch channel, renewed on a schedule with a polling fallback.

## Vendor SDK or BCL? BCL.

This repository's Google Analytics connector uses Google's client libraries; the observability and
notification companions use BCL `HttpClient`. This bridge is BCL-only, for three reasons:

- It needs six REST calls (`calendarList.get`, `events.list` / `insert` / `get` / `update` /
  `delete`, `events.watch`, `channels.stop`). The client library would replace a few lines of
  URL composition with its own request objects and save nothing else.
- The watch channel is where the SDK might have paid for itself, and it does not: a channel is
  one POST, and what makes channels hard — authenticating the notifications, renewing before
  expiry, falling back when a renewal fails — is deployment logic no SDK ships.
- Batch requests are the SDK's other advantage, and the bridge has no batch shape: the sync
  engine pushes one booking per event.

So the package references no Google assembly (GP 1), and a deployment that composes it gains no
dependency tree it did not already have.

## What it does

| `ICalendarBridge` member | Google Calendar v3 |
|---|---|
| `LinkResource` | `calendarList.get` — does it exist, can we see it? (not `calendars.get`, which `calendar.events` does not authorise) — then, when watch channels are configured, `events.watch` (unless the recorded channel still has more than `ChannelRenewalLead` to run) |
| `UnlinkResource` | `channels.stop` on the recorded channel, then forgets the channel and the sync token |
| `Push` | `events.insert` under a **deterministic event id**; an update is `events.get` + `events.update` (read-merge-write); a `Cancelled` booking is `events.delete` |
| `Pull` | `events.list` — the stored sync token when there is one, `updatedMin` otherwise, a time window when no `since` is given; paged |
| `HandleWebhook` | verifies the channel token and reports the calendar that changed |

**Idempotent push.** A created event's id is `toolup` + the booking id in base32hex — exactly
Google's event-id alphabet — so a re-push of the same booking is refused as a duplicate (409)
rather than creating a twin, and the bridge turns that refusal into the update it should have
been. The booking id also rides a private extended property.

**Read-merge-write, not PATCH.** An update fetches the event, replaces exactly the fields this
bridge manages, and writes the whole resource back. A PATCH cannot remove one entry from the
private extended-property map; a blind update would wipe what the calendar's owner set in Google
(reminders, colour, conferencing). Only the `toolup.*` private properties are the bridge's; every
other property survives.

**What maps to what.** `Title` → `summary`; the instants → `start` / `end` in UTC (with a
`timeZone`, which Google requires on a recurring event); `Recurrence` → one `RRULE:` line in
`recurrence`, through the same RRULE emitter and parser the iCalendar path uses; the reserved
`Location` metadata → `location`; `Attendees` → real Google attendees (the address minus
`mailto:`). Every other metadata entry — and the exact `Organizer` / `Attendees` strings — ride
`toolup.*` private extended properties, so a booking round-trips by value. When Google's attendee
list still matches what was pushed, the pushed spelling comes back; when someone changed it in
Google, Google's list wins.

## Capabilities it declares

- `SupportsWebhooks` — `true` exactly when `GoogleCalendarSettings.WebhookAddress` is set. With no
  address the bridge is polling-only and the sync engine's own poll job covers it.
- `SupportsIncrementalPull = true` — `since` is a MODIFICATION cursor (sync token / `updatedMin`),
  so the sync engine carries a cursor for this bridge.
- `MinimumPollInterval` — five minutes.

## Credentials

A Google Calendar credential is a **connection** on the Phase 10e OAuth substrate — the same
shape as a data-source credential. There is no sign-in UI in this package.

1. Register the flow: `ServerApp.withOAuthFlow (GoogleCalendarOAuth.create (ProviderOAuthFlow.httpPost client) secretStore (Some refresher) GoogleCalendarOAuthConfig.defaults)`.
   Flow name `google-calendar`; the substrate mounts `/api/oauth/google-calendar/authorize` and
   `/callback`.
2. The operator creates the connection as a data source of its own kind in the admin UI and
   enters the OAuth client id + secret and consents through the per-`Kind` credential form
   registry (`ClientConfig.Handlers.DataSourceCredentialHandlers`, rendered by
   `DataSourceCredentialUI.fs`). The form's save path writes the client id and secret under
   `google-calendar-client-id-<connectionId>` / `google-calendar-client-secret-<connectionId>`;
   consent is `IDataIngestionApi.BeginOAuth(connectionId, "google-calendar")`. The SDK ships no
   calendar-specific form — register one for the kind, as the Google Analytics companion does
   for its own.
3. On callback the substrate stores the refresh token under `google-calendar-refresh-<connectionId>`.

**Which connection authorises a link.** `GoogleCalendarSettings.ConnectionId = None` (the default)
— the link's `UserId` is the connection id, so each user mirrors through their own consent.
`Some id` — one shared connection (a room or service account) authorises every link.

**Tokens are read per call.** `GoogleCalendarTokenSource` resolves a bearer token on every bridge
call. With the Phase 10h `IOAuthTokenRefresher` composed, the bridge registers the connection's
refresh descriptor on first use and thereafter uses the access token the substrate keeps warm,
asking `RefreshNow` when it is missing, expiring, or refused by Google (one forced refresh per
401). Without a refresher it mints a token from the stored refresh token on every call. Either
way a rotated refresh token or client secret is picked up on the next call — no restart.

### Scopes

`https://www.googleapis.com/auth/calendar.events` (read/write events, watch channels) and
`https://www.googleapis.com/auth/calendar.calendarlist.readonly` (the link check and the health
probe — `calendarList.get` / `list` are authorised under it where `calendars.get` is not, which the
first live probe found as a 403 `ACCESS_TOKEN_SCOPE_INSUFFICIENT` on 2026-09-23). Both are
**sensitive** scopes.

## Google Cloud setup

1. Create (or pick) a Google Cloud project and enable the **Google Calendar API**.
2. Configure the **OAuth consent screen**. While the app is in *Testing*, only the test users
   you list can consent, and their refresh tokens expire after seven days. Moving to
   *In production* with sensitive scopes requires Google's **consent-screen verification** — a
   multi-day external review. The test suite never needs it.
3. Create an **OAuth client id** of type *Web application*; add the substrate's callback,
   `https://<your-host>/api/oauth/google-calendar/callback`, as an authorised redirect URI.
4. For watch channels: the notification address must be a public **HTTPS** URL with a valid
   certificate — Google will not deliver to plain HTTP or a self-signed certificate. Check
   Google's current push-notification guide for any further address requirements.

## Watch channels

```fsharp skip=fragment
open ToolUp.Calendar.GoogleCalendarOAuth
open ToolUp.Calendar.GoogleCalendar
open ToolUp.Calendar.GoogleCalendarHealth
open ToolUp.Calendar.GoogleCalendarChannels
open ToolUp.Scheduling.SchedulingCompose

let tokens =
    GoogleCalendarTokenSource(ProviderOAuthFlow.httpPost httpClient, secretStore, Some refresher, GoogleCalendarOAuthConfig.defaults)

let settings = {
    GoogleCalendarSettings.defaults with
        WebhookAddress = Some "https://app.example.com/calendar/google/notifications"
}

let bridge = GoogleCalendarBridge(tokens, secretStore, settings)

SchedulingServerApp.create ()
|> SchedulingServerApp.withConfig config
|> SchedulingServerApp.withCalendarBridge bridge
|> withChannelRenewal bridge [ "team-a"; "team-b" ] (Trigger.CronTrigger DefaultRenewalCron)
|> SchedulingServerApp.withHealthCheck (GoogleCalendarBridgeHealth(tokens, settings, "_platform", "rooms"))
|> SchedulingServerApp.run
```

Mount `GoogleCalendarChannels.handler` (a Giraffe `HttpHandler`, `POST`) at the
`WebhookAddress` path, wherever the deployment mounts its other **unauthenticated** provider
callbacks — Google's notifications carry no user session.

- **Authentication.** Every channel is created with a token
  `v1.<base64url scope>.<HMAC-SHA256>` under the deployment secret `GOOGLE_CALENDAR_CHANNEL_SECRET`
  (read from `ChannelSecretScope`, default `_platform`), over the scope and the watched calendar.
  Google echoes it on every notification (`X-Goog-Channel-Token`); the route reads the scope from
  it and the bridge verifies the MAC before anything is pulled. A notification that does not
  verify is answered `401` and changes nothing. Rotating the secret invalidates every live
  channel's token: re-link (or let the renewal job re-open channels) afterwards.
- **What a notification does.** `X-Goog-Resource-State: sync` is the opening handshake and does
  nothing; `exists` / `not_exists` pulls every link on the named calendar in that scope, from the
  link's cursor.
- **Renewal and the polling fallback.** Channels expire (the bridge asks for `ChannelTtl`, seven
  days by default; Google may grant less). `withChannelRenewal` schedules a job
  (`_calendar.google-channel-renewal`, every fifteen minutes by default) over the scopes you name:
  it re-links every Google link, which opens a fresh channel for any within `ChannelRenewalLead`
  of its end and stops the superseded one. A link whose renewal **fails** is pulled on the spot,
  so it keeps converging at the job's cadence rather than going silent.
- **Where channel state lives.** The channel record and the sync token are kept in `ISecretStore`
  in the link's scope (`google-calendar-channel-<hash>` / `google-calendar-sync-<hash>`) — the
  only store a bridge holds, which is what keeps it stateless between calls.

**Why not the generic inbound-webhook receiver?** That receiver verifies an HMAC over the request
body under one scheme per route and hands its handler the body alone. A Google notification has an
empty body, authenticates with a token in a header, and names its calendar only in headers — none
of which would reach a handler there.

## Declared limits

- **External deletions are not applied.** An event deleted in Google is reported by Google as
  cancelled; `Pull` does not return it, because the sync engine has no verb to apply an external
  deletion yet. Cancel through the deployment, which pushes the deletion. (A booking whose event
  was deleted in Google is restored there on its next push.)
- **Single-instance exceptions are skipped.** A recurring event whose one occurrence was moved in
  Google has a separate instance event (`recurringEventId`); the booking model has no per-instance
  exception, so the instance is not pulled.
- **An external edit applied locally records `BookingCreated`** unless only the times moved — the
  sync engine's limit, shared with every bridge.

## Health

`GoogleCalendarBridgeHealth` — readiness, not liveness: `users/me/calendarList?maxResults=1`
under one named connection. A refused or missing credential is `Unhealthy` (an operator must
reconnect); an unreachable Google is `Degraded` (bookings still work, they just stop mirroring).

## The probe recipe (out of suite)

Evidence that the bridge works against the real service:

1. Complete the Cloud setup above with yourself as a test user; consent once through the
   substrate (or the OAuth Playground with your own client) to obtain a refresh token.
2. Set `TOOLUP_GOOGLE_CALENDAR_TEST_CLIENT_ID`, `TOOLUP_GOOGLE_CALENDAR_TEST_CLIENT_SECRET`,
   `TOOLUP_GOOGLE_CALENDAR_TEST_REFRESH_TOKEN` and `TOOLUP_GOOGLE_CALENDAR_TEST_CALENDAR_ID`, then
   run the scheduling test pack: the live case links the calendar, pushes a booking, pulls it back
   and cancels it. With the variables unset it reports *pending*, so a fresh checkout is green.
3. For the two-way half, compose the bridge in a deployment, book a resource linked to the
   calendar, confirm the event appears in Google; edit it in Google and confirm the edit is pulled
   back under the link's conflict policy (on the next notification, or the next poll).

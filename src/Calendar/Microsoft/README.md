# Microsoft Graph calendar bridge

Phase 831 `ICalendarBridge` companion. Mirrors `ToolUp.Scheduling` bookings into Outlook /
Microsoft 365 calendars over Microsoft Graph v1.0, two ways: bookings push out as Graph events,
and edits made in Outlook come back through `calendarView` delta queries — driven by Graph
change notifications when the deployment has a public HTTPS endpoint, by polling when it does
not.

BCL `HttpClient` and `System.Text.Json` only — no Graph SDK and no Microsoft identity library.
The six endpoints this bridge uses are a smaller surface than either SDK's dependency graph, and
every request stays readable in one file.

## What it does

| `ICalendarBridge` member | Microsoft Graph |
|---|---|
| `LinkResource` | `GET /me/calendars/{id}` (exists, and writable?); with notifications on, opens or renews a subscription on the calendar's events |
| `UnlinkResource` | `DELETE /subscriptions/{id}` when one is open; always idempotent |
| `Push` | `POST` / `PATCH /me/calendars/{id}/events[/{eid}]`, or `DELETE` it when the booking is `Cancelled` |
| `Pull` | `GET /me/calendars/{id}/calendarView/delta`, then each changed event in full |
| `HandleWebhook` | verifies a Graph notification's `clientState` and names the changed calendar / event |

**The booking rides the event as two extended properties** in a ToolUp property set: the booking
id, and a JSON snapshot of what was pushed (the metadata map and the recurrence rule). The
booking id is what makes `Push` idempotent — a push with no known event id looks the booking up
by it before creating anything, and a create carries a `transactionId` derived from it, so a
create whose response was lost is not duplicated. On `Pull`, Graph's native fields win wherever
Outlook can edit them (subject, times, location, attendees, recurrence pattern); the snapshot
supplies what Graph cannot hold (every non-reserved metadata key, the `Organizer` value — Graph
always reports the mailbox owner — and the exact `RecurrenceRule` while the native pattern still
projects from it). A snapshot value survives exactly when it still matches what Graph reports,
so a push → pull round-trip is lossless and an Outlook edit is visible at once.

**Attendees are real attendees.** The reserved `Attendees` metadata key maps onto Graph's
`attendees`, so Outlook sends invitations from the mailbox that owns the calendar — the same
behaviour a scheduling CalDAV server gives the 20a carry-through. A deployment mirroring into a
resource calendar that must not invite anyone keeps attendee addresses out of
`Booking.Metadata`.

**Recurrence.** `Daily` / `Weekly` (with its weekdays) / `Monthly` / `Yearly` map onto Graph's
`daily` / `weekly` / `absoluteMonthly` / `absoluteYearly` patterns; `Until` (an exclusive
instant) onto an inclusive `endDate`, `Count` onto `numbered`. Occurrences Graph reports in a
delta are folded onto their series master — one booking, one rule. Graph's relative patterns
("the second Tuesday") have no v1 `RecurrenceRule` form and read back as a single event.

## Capabilities it declares

`SupportsWebhooks = true` when `NotificationUrl` is set (and `false` — polling only — when it
is not); `SupportsIncrementalPull = false`; `MinimumPollInterval = 5 minutes`.

The second flag is deliberate. `Pull`'s `since` argument is honoured as a lower bound on event
TIME — a fresh delta round whose window starts there — which is what the `ICalendarBridge`
contract pack's time-bound since-law checks. The delta cursor is the bridge's own: the final
`deltaLink` of a default-window pull (`since = None`) is kept per link in `ISecretStore` (the
same per-connection, per-scope state the Phase 10h refresher keeps its cached access token in)
and followed by the next default-window pull, so a notification costs one delta page rather than
a window scan. The sync engine sees only fewer unchanged events, which it already reconciles by
value. A stored cursor is abandoned for a fresh round once it is older than
`FullResyncInterval` (a day) or Graph answers `410 Gone`.

## Entra application registration (the external gate)

1. **Register an application** in Microsoft Entra ID (App registrations → New registration).
   Supported account types: whatever your users are — work/school only
   (`TOOLUP_MSGRAPH_CALENDAR_TENANT=organizations` or your tenant id), or work/school and
   personal (`common`, the default).
2. **Redirect URI** (platform *Web*): `{TOOLUP_OAUTH_REDIRECT_BASE}/api/oauth/microsoft-graph-calendar/callback`
   — the Phase 10e substrate's callback for this flow, byte for byte.
3. **Certificates & secrets → New client secret.** Store its value in `ISecretStore` under
   `MSGRAPH_CALENDAR_CLIENT_SECRET` in each scope that connects calendars (with
   `EnvironmentSecretStore`: `TOOLUP_SECRET_<SCOPE>_MSGRAPH_CALENDAR_CLIENT_SECRET`). It is read
   per call, so rotating it needs no restart.
4. **API permissions → Microsoft Graph → Delegated:** `Calendars.ReadWrite` and
   `offline_access`. No admin consent is needed for these in most tenants; users consent when
   they connect.
5. Set `TOOLUP_MSGRAPH_CALENDAR_CLIENT_ID` to the application (client) id.

## Connecting a user's calendar

No companion UI: the Phase 10e OAuth substrate and the built-in data-ingestion admin do it.

- Register the flow: `ServerApp.withOAuthFlow (MicrosoftGraphOAuth.createFlow httpClient secretStore (Some refresher) settings)`.
- Add a data source of Kind **`MicrosoftGraphCalendar`** whose id is
  `MicrosoftGraphOAuth.connectionId userId` (`msgraph-calendar-<userId>`) — the user whose
  calendar a `CalendarLinkRef` names. The data-ingestion admin kebab-cases the Kind to the flow
  name `microsoft-graph-calendar`, so its generic **Connect** button runs the authorization-code
  + PKCE round-trip against the v2.0 endpoint and the substrate stores the refresh token under
  `microsoft-graph-calendar-refresh-<connectionId>`, which is exactly what the bridge reads.
- **Disconnect** unregisters the refresh descriptor and deletes the refresh token. The v2.0
  endpoint has no per-token revocation, so `Revoke` answers `RevocationUnsupported` and the
  substrate's local deletion is the disconnect.

**Tokens flow through `IOAuthTokenRefresher` (Phase 10h), read per call.** Each bridge call reads
the refresher's cached access token and its expiry from `ISecretStore`; a missing or
nearly-expired one is refreshed through `RefreshNow` first (the connection's descriptor is
registered on first use), and a `401` from Graph forces one refresh and one re-send. The
identity platform rotates the refresh token on every refresh; the refresher persists the rotated
token before it caches the access token, so the next call — on any instance — uses it. Compose
the SDK refresher with `ServerConfig.OAuthRefresher = EnabledOAuthRefresher` (paired with the
in-process job scheduler).

## Configuration

```
TOOLUP_MSGRAPH_CALENDAR_CLIENT_ID         # required — the Entra application (client) id
TOOLUP_MSGRAPH_CALENDAR_TENANT            # optional — common (default) | organizations | consumers | tenant id
TOOLUP_MSGRAPH_CALENDAR_NOTIFICATION_URL  # optional — public HTTPS notification URL; unset = polling only
TOOLUP_MSGRAPH_CALENDAR_ENDPOINT          # optional — replaces both the Graph and sign-in base URLs
```

National clouds set `GraphEndpoint` / `LoginEndpoint` on the settings record
(`https://graph.microsoft.us` / `https://login.microsoftonline.us`, …); the window, lifetime and
resync knobs are record fields too. Secrets, all in the link's own scope:
`MSGRAPH_CALENDAR_CLIENT_SECRET`, and — only with notifications on —
`MSGRAPH_CALENDAR_WEBHOOK_SECRET`, the key every subscription's `clientState` is derived from.

## Change notifications (the public-HTTPS requirement)

With `NotificationUrl` set, `LinkResource` opens a Graph subscription on the linked calendar's
events (`created,updated,deleted`), pointed at `NotificationUrl?scope=…&calendar=…`. Graph
**validates that URL synchronously when the subscription is created** — it POSTs
`?validationToken=…` and refuses the subscription unless the endpoint answers `200 text/plain`
with the token within ten seconds — so the URL must be public HTTPS, reachable from Microsoft's
network, and the route must already be mounted. A development machine needs a tunnel.

Mount the notification route where the deployment mounts its other unauthenticated provider
callbacks, at the path of `NotificationUrl`:

```fsharp skip=fragment
POST >=> route "/webhooks/calendar-microsoft" >=> MicrosoftGraphSubscriptions.handler bridge
```

It answers the handshake, and for a notification has the bridge verify the `clientState` — an
HMAC over the URL's scope and calendar under `MSGRAPH_CALENDAR_WEBHOOK_SECRET`, so a forged body
or a genuine one replayed against another link's URL does not verify (`401`, nothing pulled) —
before the sync engine pulls the link (`202`). It is a companion route rather than the generic
inbound-webhook receiver because Graph signs nothing that receiver's HMAC schemes could check,
the handshake must be answered in the response body, and the link travels in the query string.

**Renewal.** Calendar subscriptions expire after at most about three days (the bridge asks for
two). `MicrosoftGraphSubscriptions.compose` registers an hourly `IJobScheduler` job that re-links
every Microsoft link in the given scopes — `LinkResource` renews in place and re-creates a
subscription Graph dropped — and **pulls any link whose renewal failed**, so a deployment whose
endpoint is down still mirrors at the job's cadence (delta polling) until renewal succeeds.

## Composing it

```fsharp skip=fragment
open ToolUp.Calendar.MicrosoftGraphOAuth
open ToolUp.Calendar.MicrosoftGraph
open ToolUp.Calendar.MicrosoftGraphHealth
open ToolUp.Scheduling.SchedulingCompose

let settings = MicrosoftGraphCalendarSettings.fromEnv ()
let bridge = MicrosoftGraphCalendarBridge(secretStore, refresher, settings)

SchedulingServerApp.create ()
|> SchedulingServerApp.withConfig config
|> MicrosoftGraphSubscriptions.compose bridge [ "team-a"; "team-b" ]   // the scopes that link calendars
|> SchedulingServerApp.withHealthCheck
    (MicrosoftGraphCalendarBridgeHealth(secretStore, refresher, settings, "_platform", "calendar-probe"))
|> SchedulingServerApp.run
```

`compose` is `SchedulingServerApp.withCalendarBridge` plus, when notifications are on, the
renewal job. A polling-only bridge is driven by the sync engine's own poll job and adds nothing
else (GP 13). Link a resource through the sync engine with `kind = "Microsoft"` and the Graph
calendar id as the external calendar id.

## Health

`MicrosoftGraphCalendarBridgeHealth` is a readiness probe: `GET /me/calendars?$top=1` with one
named connection's token (resolved through the refresher, like a bridge call). A credential the
platform refuses is `Unhealthy` — an operator must act; an unreachable Graph is `Degraded`,
because a calendar outage must not restart the process.

## Declared limits

- **Deletions are observed, not applied.** A delta page reports a deleted event as `@removed`;
  the sync engine has no deletion verb on the seam (Phase 20a), so the bridge drops them. A
  booking cancelled locally still removes its Graph event — that is a push.
- **Exceptions to a series** (one occurrence moved in Outlook) fold onto the series master; the
  per-occurrence edit is not mirrored.
- **The pull runs inline in the notification request.** One delta page is well inside Graph's
  three-second budget for a healthy deployment; a persistently slow endpoint gets its
  subscription dropped by Graph, which the renewal job re-creates.
- **The Phase 10h refresher's refresh request carries no `scope`.** The Microsoft identity
  platform accepts a v2.0 refresh without one and issues a token for the originally consented
  scopes; the probe below is where that is confirmed against a real tenant.

## The probe recipe (out of suite)

The test pack binds the `ICalendarBridge` contract pack to this bridge over a stub serving
canned Graph and token-endpoint responses. Against a real tenant:

1. Register the application as above, connect a test user through the data-ingestion admin, and
   note the user's calendar id (`GET /me/calendars` in Graph Explorer).
2. Set `TOOLUP_MSGRAPH_CALENDAR_TEST_CLIENT_ID`, `_CLIENT_SECRET`, `_REFRESH_TOKEN` (the stored
   refresh token), `_CALENDAR_ID` and optionally `_TENANT`, then run the scheduling pack: the
   live case links, pushes an event, pulls it back and cancels it. Unset, it reports pending.
3. For the two-way half, run a deployment with `NotificationUrl` behind a tunnel, link a
   resource with `LatestModifiedWins`, book it, confirm the event appears in Outlook, edit it
   there, and confirm the notification route answers `202` and the local booking takes the edit.

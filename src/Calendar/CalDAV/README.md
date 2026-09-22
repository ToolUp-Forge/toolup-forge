# CalDAV calendar bridge

Phase 20a `ICalendarBridge` companion. Mirrors `ToolUp.Scheduling` bookings into any
conforming CalDAV server — Fastmail, Nextcloud, iCloud, Apple Calendar Server, Radicale,
Baikal — over RFC 4791 with BCL `HttpClient` and the parent companion's own iCalendar
payloads. No vendor SDK, no paid dependency, and no OAuth application registration: it is
the bridge a deployment can turn on with a username and an app-password.

## What it does

| `ICalendarBridge` member | CalDAV |
|---|---|
| `LinkResource` | `PROPFIND Depth: 0` on the collection — does it exist, can we see it? |
| `UnlinkResource` | nothing remote (CalDAV has no subscription to cancel); always `Ok` |
| `Push` | `PUT <collection>/<bookingId>.ics`, or `DELETE` it when the booking is `Cancelled` |
| `Pull` | `REPORT calendar-query` with a `time-range` filter |
| `HandleWebhook` | no-op returning `Ok []` — CalDAV servers do not call you |

Because the event's `UID` **is** the booking id, `Push` is idempotent by construction: a
second push of the same booking overwrites the first rather than creating a twin.

## Capabilities it declares

`BridgeCapabilities.pollingOnly` — `SupportsWebhooks = false`, `SupportsIncrementalPull =
false`, `MinimumPollInterval = 15 minutes`.

The second flag is the one worth reading twice. A `calendar-query` `time-range` filters on
event TIME, not modification time, so `Pull`'s `since` argument is a lower bound on when
events START, not a change cursor. The sync engine reads that declaration and hands this
bridge no cursor; it reconciles by value instead.

## Configuration

```
TOOLUP_CALDAV_URL       # required — base URL, e.g. https://caldav.fastmail.com/
TOOLUP_CALDAV_USERNAME  # required — the account the bridge authenticates as
TOOLUP_CALDAV_ENDPOINT  # optional — replaces the base URL for every request
```

The password never appears in configuration. It is read from `ISecretStore` under
`CalDAVSettings.SecretKey` (`CALDAV_PASSWORD`) in the link's own scope, **on every call**,
so a rotated app-password takes effect without a restart and two tenants linking the same
provider never share a credential. With `EnvironmentSecretStore` that is
`TOOLUP_SECRET_<SCOPE>_CALDAV_PASSWORD`.

Most hosted providers require a server-issued app-password rather than the account
password; issue one scoped to calendar access.

## Composing it

```fsharp skip=fragment
open ToolUp.Calendar.CalDAV
open ToolUp.Calendar.CalDAVHealth
open ToolUp.Scheduling.SchedulingCompose

let settings = CalDAVSettings.fromEnv ()
let bridge = CalDAVCalendarBridge(secretStore, settings)

SchedulingServerApp.create ()
|> SchedulingServerApp.withConfig config
|> SchedulingServerApp.withCalendarBridge bridge
|> SchedulingServerApp.withHealthCheck (CalDAVCalendarBridgeHealth(secretStore, settings, "_platform"))
|> SchedulingServerApp.run
```

`withCalendarBridge` is what turns calendar sync on. A deployment that never calls it
registers no link entities, builds no sync engine and schedules no job — it is byte-for-byte
the deployment it was before (GP 13).

## Linking a resource

```fsharp skip=fragment
let! link =
    sync.LinkResource(
        scopeId,
        resourceId = "room-101",
        kind = "CalDAV",
        externalCalendarId = "/dav/calendars/user/alice/rooms/",
        userId = "alice",
        policy = LocalWins,
        actor = EntityPrincipal.ofPrincipal "scheduling-admin")
```

The external calendar id may be absolute or relative; a relative one resolves against the
configured base URL, so moving servers is a one-value change.

`policy` decides who wins when a booking changed on both sides since the last sync —
`ExternalWins`, `LocalWins`, or `LatestModifiedWins` (which compares the server's
`getlastmodified` against the instant this deployment last pushed the event, and treats an
event with no modification stamp as older, so a local edit is never silently lost).

## Two limits, stated rather than half-implemented

- **A poll does not detect external deletions.** A pull sees the events that exist; an event
  that vanished is indistinguishable from one outside the pulled window. Cancel through the
  deployment, which pushes the deletion.
- **An external edit applied locally records `BookingCreated`** unless only the times moved,
  in which case it records `BookingRescheduled`. `IBookingScheduler` exposes `Book`,
  `Cancel` and `Reschedule` and no general update verb.

## Testing against a real server

The test pack's live case runs only when `TOOLUP_CALDAV_URL` is set, so a fresh checkout is
green without credentials. [Radicale](https://radicale.org/) is the cheapest local target:
it speaks enough of RFC 4791 for every verb this bridge uses.

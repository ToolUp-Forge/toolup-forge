# ToolUp.Scheduling

Domain-neutral booking-and-availability subsystem for ToolUp.Platform. Companion package — apps that don't import `ToolUp.Scheduling.Server.props` / `ToolUp.Scheduling.Client.props` pay zero runtime cost.

## What this is

A small, opinionated booking primitive: **bookable resources** with **default availability** and **availability exceptions**, **bookings** that can recur via **RFC 5545 RRULE**, and **conflict detection** that surfaces every reason at once. Plus an **iCalendar (`.ics`) parser + emitter** for export / import.

Shipped surface (commit-stable across Phase 20):

| Concern | File | Module |
|---|---|---|
| Domain types | [`Shared/SchedulingTypes.fs`](Shared/SchedulingTypes.fs) | `BookableResource`, `Booking`, `AvailabilityException`, `RecurrenceRule`, `BookingConflict`, `BookingError` |
| Recurrence expansion | [`Shared/RecurrenceExpander.fs`](Shared/RecurrenceExpander.fs) | `expand`, `occurrenceStarts` — pure, Fable-safe |
| Wire contract | [`Shared/SchedulingApi.fs`](Shared/SchedulingApi.fs) | `ISchedulingApi` (ToolUp.Remoting) |
| iCalendar I/O | [`Shared/iCalendar.fs`](Shared/iCalendar.fs) | `parse`, `emit`, `VCalendar`, `VEvent` |
| Audit payloads | [`Shared/SchedulingEvents.fs`](Shared/SchedulingEvents.fs) | `BookingCreatedPayload` etc., `SourceModule = "_scheduling"` |
| Server interface | [`Server/IBookingScheduler.fs`](Server/IBookingScheduler.fs) | `IBookingScheduler` — six-rule portable |
| Pure conflict detection | [`Server/BookingConflictDetector.fs`](Server/BookingConflictDetector.fs) | `detect`, `DetectorInputs` |
| Default impl | [`Server/BookingScheduler.fs`](Server/BookingScheduler.fs) | `BookingScheduler` over `IEntityStore` + `IEventStore` |
| API handler | [`Server/SchedulingApiHandler.fs`](Server/SchedulingApiHandler.fs) | `schedulingApi` ToolUp.Remoting handler |
| Compose pipeline | [`Server/SchedulingCompose.fs`](Server/SchedulingCompose.fs) | `SchedulingServerApp` record + `run` |

## Why a companion, not core SDK

Booking is a **domain capability**, not platform substrate. Nothing else in the SDK depends on `IBookingScheduler`. Analytics-only deployments never need scheduling — charging them the binary weight is wrong-by-default. This is the same rationale for `ToolUp.AI` / `ToolUp.RAG` / `ToolUp.KnowledgeBase` not living in `ToolUp.Platform`.

The companion is a *consumer* of substrate (`IEntityStore` Phase 19, `IEventStore` Phase 6, optionally `IJobScheduler` Phase 9b for reminders), not substrate itself.

## How to enable

In your server `.fsproj`:

```xml
<Import Project="..\ToolUp.Scheduling\ToolUp.Scheduling.Server.props" />
```

In your client `.fsproj` (only if you'll use the planned Feliz components):

```xml
<Import Project="..\ToolUp.Scheduling\ToolUp.Scheduling.Client.props" />
```

In your server's composition root:

```fsharp skip=fragment
open ToolUp.Scheduling.SchedulingCompose

let config = {
    ServerConfig.defaults with
        Port = 5000
        Surfaces = Surfaces.team
        EntityStore = EnabledEntityStore   // REQUIRED — scheduling rides on Phase 19
}

[<EntryPoint>]
let main _ =
    SchedulingServerApp.create ()
    |> SchedulingServerApp.withConfig config
    |> SchedulingServerApp.withAuth (StaticJwtAuthProvider(...))
    |> SchedulingServerApp.withStorage (LocalFileStorage("data"))
    |> SchedulingServerApp.addModules myModules
    |> SchedulingServerApp.run
```

`SchedulingServerApp.run`:
1. Registers three entity types with `IEntityStore` via `ServerApp.withEntity`:
   - `BookableResource` (index on `ResourceType`)
   - `Booking` (indexes on `ResourceId`, `Status`, compound `(ResourceId, StartUtc)`)
   - `AvailabilityException` (index on `ResourceId`)
2. Registers `IBookingScheduler` in DI as a singleton (default impl: `BookingScheduler`).
3. Mounts the `ISchedulingApi` ToolUp.Remoting handler.
4. Delegates the rest to `ServerApp.run`.

Apps without `ServerConfig.EntityStore = EnabledEntityStore` get a runtime null reference at first dispatch — explicit enforcement is a follow-up.

## How to register a resource type

`BookableResource.ResourceType` is a free-form string (`"Person"`, `"Room"`, `"Equipment"`, or your domain's term). The SDK never names a resource type — that's the deployment's choice. The same `IBookingScheduler` instance handles every resource type the deployment uses.

```fsharp skip=fragment
let alice : BookableResource = {
    Id = "alice@team"
    Type = "BookableResource"
    Version = 1
    ResourceType = "Person"
    DisplayName = "Alice"
    Timezone = "Europe/London"
    DefaultAvailability = [
        {
            DayOfWeek = None                  // every day
            StartTime = TimeOnly(9, 0)
            EndTime = TimeOnly(17, 0)
            EffectiveFrom = None
            EffectiveTo = None
        }
    ]
    Metadata = Map.empty
}

scheduler.RegisterResource("team-acme", alice)
```

## How to configure reminders

Reminder fusion is **opt-in** and currently a no-op flag (`SchedulingServerApp.withReminders`). When the feature lands, the consumer enables it via:

```fsharp
SchedulingServerApp.create ()
|> SchedulingServerApp.withReminders        // future: lead-time + transport config
|> SchedulingServerApp.run
```

Until then, deployments that want booking reminders dispatch them themselves via `IJobScheduler` against `_scheduling` events — same plumbing the future fusion will use.

## RRULE subset

`RecurrenceExpander` and the iCalendar layer cover:
- `FREQ=DAILY|WEEKLY|MONTHLY|YEARLY`
- `INTERVAL=N`
- `BYDAY=MO,TU,...,SU` (Weekly only — token weekdays, no positional offsets)
- `UNTIL=YYYYMMDDTHHMMSSZ`
- `COUNT=N`

Plus a hard cap of **10,000 occurrences** to defend against pathological rules. Caller detects the cap by comparing the returned list length to the hard cap.

Not supported: positional `BYDAY` (`1MO`, `-1FR`), `BYMONTHDAY`, `BYSETPOS`, sub-day frequencies. Deferred to Phase 20 follow-ups if customer demand emerges.

## iCalendar coverage

Parser + emitter cover the **VEVENT subset**: `UID` / `SUMMARY` / `DTSTART` / `DTEND` / `DTSTAMP` / `RRULE` / `DESCRIPTION`. Datetime forms: UTC (`YYYYMMDDTHHMMSSZ`), floating + TZID (lifted to UTC via `TimeZoneInfo.FindSystemTimeZoneById`), date-only (midnight UTC). Line discipline: parser unfolds CRLF or LF + leading-whitespace; emitter folds at 75 octets. Round-trip-tested against synthetic Apple Calendar / Outlook / Google Calendar fixture excerpts (see [`Tests/InProcess/iCalendarTests.fs`](../ToolUp.Scheduling.Tests/InProcess/iCalendarTests.fs)).

Not supported: VTODO / VJOURNAL / VFREEBUSY / VTIMEZONE definitions, X-properties (silently dropped on parse, never emitted), positional `BYDAY` / `BYMONTHDAY` / `BYSETPOS`, ATTENDEE / ORGANIZER. No paid dependencies (RFC 5545 is open; pure F# implementation, no `Ical.Net`).

## See also

- [`TECHNICAL_GUIDE.md`](TECHNICAL_GUIDE.md) — internals, six-rule portability audit verdict, conflict-detection algorithm, simplifications + deferrals.
- [`../ToolUp.Platform/Server/IEntityStore.fs`](../ToolUp.Platform/Server/IEntityStore.fs) — entity-store substrate.

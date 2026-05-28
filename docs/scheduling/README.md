# ToolUp.Scheduling

Booking scheduler for ToolUp Platform. Single-resource booking with concurrency lock, conflict detection, recurrence expansion, iCalendar export. Smaller surface than other companions — use it when you need a booking calendar; skip otherwise.

## When to use this companion

- **Appointment booking** — hairdressers, therapists, coaches, personal trainers.
- **Resource reservation** — meeting rooms, equipment, vehicles.
- **Anything with "available slots" + "claim a slot"** — the per-resource concurrency lock prevents double-booking.

## When NOT to use this companion

- **Calendar event tracking** (no claims, no contention) — use `IDataObjectStore` directly with `VersioningPolicy = Versioned`. Scheduling is overkill.
- **Long-running async jobs** — use `ToolUp.Platform.IJobScheduler`.
- **Event-driven workflows** without resource contention — use `ToolUp.Forms` workflows.
- **Multi-resource optimisation** (assigning N jobs to M workers under constraints) — needs a proper optimisation solver, not a scheduler.

## What's in the box

Two packages:

| Package | What it is |
|---|---|
| `ToolUp.Scheduling.Core` | Shared types: `Slot`, `Booking`, `Resource`, `RecurrenceRule`, `iCalendar` export, `ISchedulingApi` Fable.Remoting contract. |
| `ToolUp.Scheduling.Server` | `IBookingScheduler` interface with per-resource concurrency lock, conflict detector, scheduling API handler, `SchedulingCompose`. |

## Quick start

Add the packages:

```xml
<PackageReference Include="ToolUp.Scheduling.Server" />
<PackageReference Include="ToolUp.Scheduling.Core" />   <!-- transitive; explicit if you reference types directly -->
```

Define a resource:

```fsharp
open ToolUp.Scheduling

let salonChair : Resource = {
    ResourceId = ResourceId "chair-1"
    Name = "Stylist Chair 1"
    AvailabilityWindows = [
        // Mon-Fri 09:00-17:00
        { Days = [ DayOfWeek.Monday; Tuesday; Wednesday; Thursday; Friday ]
          StartTime = TimeSpan(9, 0, 0)
          EndTime = TimeSpan(17, 0, 0)
          Timezone = "Europe/London" }
    ]
    SlotDurationMinutes = 60
    BufferBetweenSlotsMinutes = 0
}
```

Wire the server composition root:

```fsharp
open ToolUp.Scheduling

ServerApp.empty
|> ServerApp.withConfig serverConfig
|> ServerApp.withAuth authProvider
|> ServerApp.addModules modules
|> SchedulingServerApp.fromServerApp
|> SchedulingServerApp.withResource salonChair
|> SchedulingServerApp.run
```

Wire the client (no built-in calendar UI — the SDK ships the data primitives; the calendar grid is your module's UI).

Book a slot:

```fsharp
let! result = schedulingApi.Book {
    ResourceId = ResourceId "chair-1"
    Start = DateTime(2026, 5, 12, 14, 0, 0)
    End = DateTime(2026, 5, 12, 15, 0, 0)
    Notes = Some "Customer: Jane Smith"
}
// result : Result<Booking, BookingError>
```

`BookingError`:
- `OutsideAvailability` — slot is outside the resource's `AvailabilityWindows`.
- `SlotOccupied` — another booking already claims overlapping time.
- `ResourceNotFound`
- `Forbidden`

## Per-resource concurrency lock

`BookingScheduler.Book` uses a `SemaphoreSlim` per `ResourceId` to serialise booking attempts against the same resource. Two callers booking the same slot at the same instant get one success + one `SlotOccupied` — never two successes.

The lock is per-resource, not global — different resources book concurrently. Scales to hundreds of resources without contention.

## Recurrence

`RecurrenceRule` is RFC 5545–inspired:

```fsharp
type RecurrenceRule = {
    Frequency: Frequency             // Daily | Weekly | Monthly | Yearly
    Interval: int                    // every N units
    ByDayOfWeek: DayOfWeek list      // for Weekly
    ByDayOfMonth: int list           // for Monthly
    Count: int option                // total occurrences
    Until: DateTime option           // last occurrence
}
```

Use `RecurrenceExpander.expand` to materialise a recurrence into concrete dates:

```fsharp
let weeklyTherapy = {
    Frequency = Weekly
    Interval = 1
    ByDayOfWeek = [ DayOfWeek.Tuesday ]
    Count = Some 12
    Until = None
}

let dates =
    RecurrenceExpander.expand
        weeklyTherapy
        (startDate = DateTime(2026, 5, 12))
// : DateTime list
```

The expander is pure — no I/O, no scheduling impl. Use it client-side to render a series of slots; use it server-side to book a series in one call.

## iCalendar export

```fsharp
let! ics = schedulingApi.ExportICalendar resourceId

// ics : string  (RFC 5545-compliant .ics file content)
```

Drop into an `.ics` download endpoint. Calendars (Google, Outlook, Apple) consume it. Useful for "subscribe to my booking calendar" + per-customer "your upcoming appointments" exports.

## Concepts

See [concepts.md](concepts.md) for the data model, concurrency model, recurrence semantics, iCalendar wire format.

## API reference

See [api-reference.md](api-reference.md) for the full surface.

## Extending

See [extending.md](extending.md) for custom `IBookingScheduler` impls, multi-resource booking patterns, calendar UI components.

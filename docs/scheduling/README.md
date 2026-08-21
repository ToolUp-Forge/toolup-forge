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
| `ToolUp.Scheduling.Core` | Shared types: `Slot`, `Booking`, `Resource`, `RecurrenceRule`, `iCalendar` export, `ISchedulingApi` ToolUp.Remoting contract. |
| `ToolUp.Scheduling.Server` | `IBookingScheduler` interface with per-resource concurrency lock, conflict detector, scheduling API handler, `SchedulingCompose`. |

## Quick start

Add the packages:

```xml
<PackageReference Include="ToolUp.Scheduling.Server" />
<PackageReference Include="ToolUp.Scheduling.Core" />   <!-- transitive; explicit if you reference types directly -->
```

Define a resource:

A resource carries a weekly availability pattern; a slot length is chosen per query rather than baked into the resource.

```fsharp
let weekday (d: DayOfWeek) : AvailabilityWindow = {
    DayOfWeek = Some d
    StartTime = TimeOnly(9, 0)
    EndTime = TimeOnly(17, 0)
    EffectiveFrom = None
    EffectiveTo = None
}

let salonChair: BookableResource = {
    Id = "chair-1"
    Type = "BookableResource"
    Version = 0
    ResourceType = "Equipment"
    DisplayName = "Stylist Chair 1"
    Timezone = "Europe/London"
    DefaultAvailability = [
        for d in
            [
                DayOfWeek.Monday
                DayOfWeek.Tuesday
                DayOfWeek.Wednesday
                DayOfWeek.Thursday
                DayOfWeek.Friday
            ] -> weekday d
    ]
    Metadata = Map.empty
}
```

Wire the server composition root. There is no compose-time resource list — register resources at runtime, into the caller's scope:

```fsharp skip=fragment
SchedulingServerApp.create ()
|> SchedulingServerApp.withConfig serverConfig
|> SchedulingServerApp.withAuth authProvider
|> SchedulingServerApp.addModules modules
|> SchedulingServerApp.run
```

Wire the client (no built-in calendar UI — the SDK ships the data primitives; the calendar grid is your module's UI).

Book a slot:

```fsharp skip=fragment
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
    Frequency: RecurrenceFrequency   // Daily | Weekly | Monthly | Yearly
    Interval: int                    // every N units
    ByWeekday: DayOfWeek list        // filters Weekly emissions; ignored otherwise
    Until: DateTimeOffset option     // exclusive upper bound
    Count: int option                // total occurrences, including the seed
}
```

v1 deliberately omits sub-day frequencies and the complex monthly rules (`BySetPos` / `ByMonthDay`). Use `RecurrenceExpander.occurrenceStarts` to materialise a rule into concrete instants:

```fsharp
let weeklyTherapy: RecurrenceRule = {
    Frequency = Weekly
    Interval = 1
    ByWeekday = [ DayOfWeek.Tuesday ]
    Count = Some 12
    Until = None
}

let starts =
    RecurrenceExpander.occurrenceStarts
        (DateTimeOffset(2026, 5, 12, 9, 0, 0, TimeSpan.Zero))
        weeklyTherapy
        (DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero))
// : DateTimeOffset list
```

The expander is pure — no I/O, no scheduling impl. `RecurrenceExpander.expand` is the booking-shaped twin: it takes a seed `Booking` and a `DateRange` window and returns the occurrence bookings. Both are bounded by a hard cap of 10,000 occurrences.

## iCalendar

The `iCalendar` module round-trips an RFC 5545 subset in both directions — `parse` / `emit` over `VCalendar`, and `bookingToVEvent` / `vEventToBooking` to map to and from the booking model.

```fsharp skip=fragment
let ics =
    iCalendar.emit {
        Version = "2.0"
        ProdId = iCalendar.CanonicalProdId
        Events = bookings |> List.map iCalendar.bookingToVEvent
    }
```

Drop into an `.ics` download endpoint of your own — the SDK does not auto-inject one. Calendars (Google, Outlook, Apple) consume it. `bookingToVEvent` is lossy by design: `Status`, `BookedBy`, `BookedFor`, `ParentBookingId` and `Metadata` have no iCal representation, and `vEventToBooking` restores them from a `defaults` booking on import.

## Concepts

See [concepts.md](concepts.md) for the data model, concurrency model, recurrence semantics, iCalendar wire format.

## API reference

See [api-reference.md](api-reference.md) for the full surface.

## Extending

See [extending.md](extending.md) for custom `IBookingScheduler` impls, multi-resource booking patterns, calendar UI components.

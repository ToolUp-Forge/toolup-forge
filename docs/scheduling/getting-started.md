# Getting started with ToolUp.Scheduling

End-to-end walkthrough: define a bookable resource, book a slot, render a calendar.

## Prerequisites

- A working ToolUp Platform app.
- `IEntityStore` enabled — `ServerConfig.EntityStore = EnabledEntityStore`. Resources and bookings persist as entities.

## 1. Add the packages

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.Scheduling.Server" />
</ItemGroup>
```

## 2. Define a resource

A `BookableResource` represents one bookable thing — a chair, a room, a person's calendar, a piece of equipment. The resource carries the timezone once; window times are local to it.

```fsharp
open ToolUp.Scheduling

let window (day: DayOfWeek) (fromH: int) (toH: int) : AvailabilityWindow = {
    DayOfWeek = Some day
    StartTime = TimeOnly(fromH, 0)
    EndTime = TimeOnly(toH, 0)
    EffectiveFrom = None
    EffectiveTo = None
}

let stylistOne: BookableResource = {
    Id = "stylist-1"
    Type = "BookableResource"
    Version = 0
    ResourceType = "Person"
    DisplayName = "Jane (Senior Stylist)"
    Timezone = "Europe/London"
    DefaultAvailability = [
        // Mon-Fri 09:00-17:00 local
        window DayOfWeek.Monday 9 17
        window DayOfWeek.Tuesday 9 17
        window DayOfWeek.Wednesday 9 17
        window DayOfWeek.Thursday 9 17
        window DayOfWeek.Friday 9 17
        // Saturday 10:00-14:00
        window DayOfWeek.Saturday 10 14
    ]
    Metadata = Map [ "speciality", "Cuts, colours, consultations" ]
}
```

`AvailabilityWindow`s repeat weekly (`DayOfWeek = None` means every day). One-off blackouts and one-off extra hours are separate `AvailabilityException` records against a specific date, not a list on the resource. Slot length is chosen per query rather than baked into the resource.

## 3. Wire `SchedulingServerApp`

```fsharp skip=fragment
SchedulingServerApp.create ()
|> SchedulingServerApp.withConfig {
    ServerConfig.defaults with
        EntityStore = EnabledEntityStore
}
|> SchedulingServerApp.withAuth authProvider
|> SchedulingServerApp.addModules modules
|> SchedulingServerApp.run
```

That's it. The scheduling API + persistence is now in place. Register `stylistOne` at runtime with `ISchedulingApi.RegisterResource` — resources are per-scope data, not compose-time configuration.

## 4. List slots for a date range

Client-side:

```fsharp skip=fragment
let! slots =
    SchedulingClient.proxy.ListSlots {
        ResourceId = ResourceId "stylist-1"
        Start = DateTime(2026, 5, 12)
        End = DateTime(2026, 5, 19)    // one week
    }
// slots : Slot list (each Free | Booked | Blocked)
```

`Slot`:

```fsharp
type Slot = {
    ResourceId: ResourceId
    Start: DateTime
    End: DateTime
    Status: SlotStatus
}

and SlotStatus =
    | Free
    | Booked of BookingId
    | Blocked of reason: string
```

The server derives slots from the resource's `AvailabilityWindows` + buffer + existing bookings. Free slots are bookable; Booked / Blocked aren't.

## 5. Book a slot

```fsharp skip=fragment
let! result =
    SchedulingClient.proxy.Book {
        ResourceId = ResourceId "stylist-1"
        Start = DateTime(2026, 5, 12, 14, 0, 0)
        End = DateTime(2026, 5, 12, 15, 0, 0)
        Notes = Some "Customer: Jane Smith — colour + cut"
    }

match result with
| Ok booking ->
    // Booking confirmed — booking.BookingId is the reference
    ...
| Error OutsideAvailability ->
    // Slot is outside the stylist's availability windows
    ...
| Error SlotOccupied ->
    // Another booking claimed overlapping time (concurrent caller won the race)
    ...
| Error ResourceNotFound -> ...
| Error Forbidden -> ...
```

The server's per-resource `SemaphoreSlim` ensures two concurrent callers booking the same slot get one success + one `SlotOccupied`. No double-bookings.

## 6. Cancel a booking

```fsharp skip=fragment
let! result = schedulingApi.Cancel(bookingId, "Customer rang to cancel")
// result : Result<unit, BookingError>
```

`Cancel` takes the reason alongside the id, and is idempotent — cancelling twice succeeds and emits `BookingCancelled` only on the first transition. A cancelled booking is ignored by conflict detection, so its window frees up for re-booking.

## 7. Render a calendar grid (UI)

The SDK ships no built-in calendar UI — the data primitives let you render whatever grid your module needs:

```fsharp skip=fragment
open Feliz

// slots : TimeSlot list — FindAvailableSlots emits only free windows, so
// there is no per-slot status to branch on.
let calendarView (slots: TimeSlot list) =
    Html.div [
        prop.className "calendar-grid"
        prop.children [
            for slot in slots do
                Html.div [
                    prop.className "slot-free"
                    prop.text (slot.Start.ToString("HH:mm"))
                    prop.onClick (fun _ -> bookSlot slot)
                ]
        ]
    ]
```

For complex calendar shapes (week view, month view, drag-to-extend slot), use a Feliz wrapper around a calendar component library (FullCalendar, React Big Calendar). The SDK doesn't bundle one; consumers pick what fits.

## 8. Recurring bookings

For recurring appointments ("weekly therapy session for 12 weeks"):

```fsharp skip=fragment
let weeklyRule = {
    Frequency = Weekly
    Interval = 1
    ByDayOfWeek = [ DayOfWeek.Tuesday ]
    ByDayOfMonth = []
    Count = Some 12
    Until = None
}

let dates =
    RecurrenceExpander.expand
        weeklyRule
        (startDate = DateTime(2026, 5, 12))

// Book each date
for date in dates do
    let! _ = SchedulingClient.proxy.Book {
        ResourceId = stylistId
        Start = date.AddHours(14.)
        End = date.AddHours(15.)
        Notes = Some "Recurring — Jane Smith"
    }
```

If any individual booking fails (slot occupied, outside availability), the loop continues; the caller decides whether to roll back the already-booked dates or partially proceed.

For atomic series booking (all-or-nothing), use `BookSeries`:

```fsharp skip=fragment
let! result = SchedulingClient.proxy.BookSeries {
    ResourceId = stylistId
    DurationMinutes = 60
    Recurrence = weeklyRule
    StartDate = DateTime(2026, 5, 12)
    StartTime = TimeSpan(14, 0, 0)
    Notes = Some "Recurring"
}

// result : Result<Booking list, BookingError * DateTime list>
//   Ok bookings = all succeeded
//   Error (err, conflictDates) = none booked; conflicts are the dates that would fail
```

`BookSeries` either books every occurrence or none. Useful for "all 12 weeks must work, or I'll pick a different time".

## 9. Export to iCalendar

There is no `ExportICalendar` call on the API — the `iCalendar` module is a pure codec, and serving `.ics` is your module's route to write:

```fsharp skip=fragment
let ics (bookings: Booking list) =
    iCalendar.emit {
        Version = "2.0"
        ProdId = iCalendar.CanonicalProdId
        Events = bookings |> List.map iCalendar.bookingToVEvent
    }
```

Serve it as a download:

```fsharp skip=fragment
let icalRoute: HttpHandler =
    fun next ctx -> task {
        let resourceId = ctx.Request.Query["resource"].ToString()
        let scheduler = ctx.RequestServices.GetRequiredService<IBookingScheduler>()
        let! bookings = scheduler.ListBookings(scopeId, resourceId, window) |> Async.StartAsTask
        ctx.Response.Headers.ContentType <- "text/calendar"
        ctx.Response.Headers.ContentDisposition <- "attachment; filename=bookings.ics"
        return! ctx.Response.WriteAsync(ics bookings)
    }
```

`bookingToVEvent` is lossy — `Status`, `BookedBy`, `BookedFor`, `ParentBookingId` and `Metadata` have no iCal representation. That is the right trade for a calendar client that does not speak ToolUp; use `vEventToBooking` with a `defaults` booking to restore them on import.

## Next steps

- [concepts.md](concepts.md) — data model, concurrency model, recurrence semantics, iCalendar wire format.
- [api-reference.md](api-reference.md) — full public surface.
- [extending.md](extending.md) — custom `IBookingScheduler` impls, multi-resource patterns, calendar UI.

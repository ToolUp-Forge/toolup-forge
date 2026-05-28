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

A `Resource` represents one bookable thing — a chair, a room, a person's calendar, a piece of equipment.

```fsharp
open ToolUp.Scheduling

let stylistOne : Resource = {
    ResourceId = ResourceId "stylist-1"
    Name = "Jane (Senior Stylist)"
    Description = Some "Cuts, colours, consultations"
    AvailabilityWindows = [
        // Mon-Fri 09:00-17:00 UK time
        { Days = [ DayOfWeek.Monday; Tuesday; Wednesday; Thursday; Friday ]
          StartTime = TimeSpan(9, 0, 0)
          EndTime = TimeSpan(17, 0, 0)
          Timezone = "Europe/London" }
        // Saturday 10:00-14:00
        { Days = [ DayOfWeek.Saturday ]
          StartTime = TimeSpan(10, 0, 0)
          EndTime = TimeSpan(14, 0, 0)
          Timezone = "Europe/London" }
    ]
    BlockedTimes = []                  // ad-hoc blackouts (holidays, sick days)
    SlotDurationMinutes = 60
    BufferBetweenSlotsMinutes = 15     // 15-min gap between back-to-back bookings
}
```

`AvailabilityWindow`s are recurring weekly. `BlockedTimes` overlay one-off blackouts. Slot duration + buffer determine the candidate-slot grid.

## 3. Wire `SchedulingServerApp`

```fsharp
ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        EntityStore = EnabledEntityStore
}
|> ServerApp.withAuth authProvider
|> ServerApp.addModules modules
|> SchedulingServerApp.fromServerApp
|> SchedulingServerApp.withResource stylistOne
|> SchedulingServerApp.run
```

That's it. The scheduling API + persistence is now in place.

## 4. List slots for a date range

Client-side:

```fsharp
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

```fsharp
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

```fsharp
let! result = SchedulingClient.proxy.Cancel bookingId
// result : Result<unit, BookingError>
```

Cancelled bookings free the slot for re-booking. Audit log records `BookingCancelled`.

## 7. Render a calendar grid (UI)

The SDK ships no built-in calendar UI — the data primitives let you render whatever grid your module needs:

```fsharp
open Feliz

let calendarView slots =
    Html.div [
        prop.className "calendar-grid"
        prop.children [
            for slot in slots do
                Html.div [
                    prop.className (
                        match slot.Status with
                        | Free -> "slot-free"
                        | Booked _ -> "slot-booked"
                        | Blocked _ -> "slot-blocked")
                    prop.text (slot.Start.ToString("HH:mm"))
                    prop.onClick (fun _ ->
                        if slot.Status = Free then bookSlot slot)
                ]
        ]
    ]
```

For complex calendar shapes (week view, month view, drag-to-extend slot), use a Feliz wrapper around a calendar component library (FullCalendar, React Big Calendar). The SDK doesn't bundle one; consumers pick what fits.

## 8. Recurring bookings

For recurring appointments ("weekly therapy session for 12 weeks"):

```fsharp
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

```fsharp
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

```fsharp
let! ics = SchedulingClient.proxy.ExportICalendar resourceId
// ics : string (.ics content)
```

Serve as a download:

```fsharp
let icalRoute : HttpHandler =
    fun next ctx -> task {
        let resourceId = ResourceId (ctx.Request.Query.["resource"].ToString())
        let! ics = serviceProvider.GetRequiredService<IBookingScheduler>().ExportICalendar resourceId
        ctx.Response.Headers.ContentType <- "text/calendar"
        ctx.Response.Headers.ContentDisposition <- "attachment; filename=bookings.ics"
        return! ctx.Response.WriteAsync(ics) |> Async.AwaitTask
    }
```

Or expose it directly in your module's API.

## Next steps

- [concepts.md](concepts.md) — data model, concurrency model, recurrence semantics, iCalendar wire format.
- [api-reference.md](api-reference.md) — full public surface.
- [extending.md](extending.md) — custom `IBookingScheduler` impls, multi-resource patterns, calendar UI.

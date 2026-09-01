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

```fsharp
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

## 4. Find free slots in a date range

Client-side. There is no shipped `ToolUp.Scheduling.Client` package and no shipped proxy value — the consumer builds its own `ISchedulingApi` proxy over `SchedulingApi.routeBuilder` (see [api-reference.md](api-reference.md#client-tier)) and calls through it:

```fsharp
let weekOfSlots = async {
    let oneWeek: DateRange = {
        Start = DateTimeOffset(2026, 5, 12, 0, 0, 0, TimeSpan.Zero)
        End = DateTimeOffset(2026, 5, 19, 0, 0, 0, TimeSpan.Zero)
    }

    let! slots =
        schedulingApi.FindAvailableSlots {
            ResourceId = "stylist-1"
            Window = oneWeek
            SlotDurationMinutes = 60
        }

    // slots : TimeSlot list — only FREE windows are emitted
    return slots
}
```

Slot length is a property of the query (`SlotDurationMinutes`), not of the resource. `TimeSlot`:

```fsharp
type TimeSlot = {
    Start: DateTimeOffset
    End: DateTimeOffset
    ResourceId: ResourceId
}
```

The server derives slots from the resource's `DefaultAvailability` windows, subtracts existing bookings (ignoring `Cancelled` and `NoShow`), subtracts `FullDay` / `PartialBlock` exceptions, adds `ExtendedHours` exceptions, and emits a slot on every surviving boundary. **A slot carries no status** — an occupied or blocked window simply is not emitted, so there is nothing to branch on.

## 5. Book a slot

```fsharp
let bookColourAndCut = async {
    let! result =
        schedulingApi.Book {
            Id = Guid.NewGuid().ToString()
            // The entity-store discriminator — the constant the three
            // scheduling registrations use, never a domain label.
            Type = "Booking"
            Version = 0
            ResourceId = "stylist-1"
            Title = "Jane Smith — colour + cut"
            StartUtc = DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.Zero)
            EndUtc = DateTimeOffset(2026, 5, 12, 15, 0, 0, TimeSpan.Zero)
            Status = Confirmed
            BookedBy = currentUserId
            BookedFor = None
            Recurrence = None
            ParentBookingId = None
            Metadata = Map.empty
        }

    match result with
    | Ok booking -> printfn $"Booked — reference {booking.Id}"

    // Every schedule disagreement arrives as ONE `Conflicts` case
    // carrying the full list, so the UI can surface every reason at
    // once rather than only the first.
    | Error(Conflicts conflicts) ->
        for conflict in conflicts do
            match conflict with
            | OverlappingBooking existing -> printfn $"Overlaps booking {existing}"
            | OutsideAvailability _ -> printfn "Outside the stylist's availability windows"
            | ResourceUnavailable exc -> printfn $"Blocked by an availability exception on {exc.Date}"
            | RecurrenceOverflow(_, emitted) -> printfn $"Recurrence expanded past the cap after {emitted}"

    | Error(UnknownResource id) -> printfn $"No resource {id} in this scope"
    | Error(UnknownBooking id) -> printfn $"No booking {id} in this scope"
    | Error(InvalidRecurrence message)
    | Error(InvalidWindow message)
    | Error(StorageFailure message) -> printfn $"Booking failed: {message}"
}
```

`BookingError` has no `SlotOccupied`, `ResourceNotFound` or `Forbidden` case — the first is a `BookingConflict` (`OverlappingBooking`) delivered inside `Conflicts`, the second is `UnknownResource`, and authorisation is not a `BookingError` at all: the handler classifies the whole `ISchedulingApi` surface `AllowAnonymous` and relies on `StorageScope` isolation, so a caller never sees a per-method refusal here.

**The shipped `BookingScheduler` does not serialise concurrent bookings.** `Book` runs conflict detection and then saves, with no per-resource lock, so two genuinely concurrent callers can both pass detection and both persist. Deployments that need strict no-double-booking replace `IBookingScheduler` with an implementation that takes a distributed lock around detect-then-save — see [extending.md](extending.md).

## 6. Cancel a booking

```fsharp
let cancel = async {
    let! result = schedulingApi.Cancel(bookingId, "Customer rang to cancel")
    // result : Result<unit, BookingError>
    return result
}
```

`Cancel` takes the reason alongside the id, and is idempotent — cancelling twice succeeds and emits `BookingCancelled` only on the first transition. A cancelled booking is ignored by conflict detection, so its window frees up for re-booking.

## 7. Render a calendar grid (UI)

The SDK ships no built-in calendar UI — the data primitives let you render whatever grid your module needs:

```fsharp
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

```fsharp
let weeklyRule: RecurrenceRule = {
    Frequency = Weekly
    Interval = 1
    ByWeekday = [ DayOfWeek.Tuesday ]
    Count = Some 12
    Until = None
}

// `expand` is pure, and it takes a SEED BOOKING rather than a start
// date: each occurrence is the seed with `StartUtc` / `EndUtc` shifted,
// so the duration and every other field carry over unchanged.
let seed = {
    baseBooking with
        StartUtc = DateTimeOffset(2026, 5, 12, 14, 0, 0, TimeSpan.Zero)
        EndUtc = DateTimeOffset(2026, 5, 12, 15, 0, 0, TimeSpan.Zero)
        Recurrence = Some weeklyRule
}

let searchWindow: DateRange = {
    Start = DateTimeOffset(2026, 5, 12, 0, 0, 0, TimeSpan.Zero)
    End = DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero)
}

let occurrences = RecurrenceExpander.expand seed weeklyRule searchWindow

// Book each occurrence. Each needs its own id — `expand` clones the
// seed, so every occurrence arrives carrying the seed's.
let bookEach = async {
    for occurrence in occurrences do
        let! _ = schedulingApi.Book { occurrence with Id = Guid.NewGuid().ToString() }
        ()
}
```

The window is the third bound, alongside the rule's own `Count` and `Until`; expansion also stops at a hard cap of 10,000 occurrences. If any individual booking fails (conflict, outside availability), the loop continues; the caller decides whether to roll back the already-booked dates or partially proceed.

There is **no `BookSeries` call** — `ISchedulingApi` books one occurrence at a time. "All twelve weeks or none" is a caller-side two-phase over `DetectConflicts`:

```fsharp
let bookSeries (occurrences: Booking list) = async {
    let! probes =
        occurrences
        |> List.map (fun occurrence -> async {
            let! conflicts = schedulingApi.DetectConflicts occurrence
            return occurrence, conflicts
        })
        |> Async.Sequential

    let clashes =
        probes
        |> Array.filter (fun (_, conflicts) -> not (List.isEmpty conflicts))
        |> Array.map (fun (occurrence, _) -> occurrence.StartUtc)
        |> List.ofArray

    if not (List.isEmpty clashes) then
        // Nothing was booked — `clashes` are the dates that would fail.
        return Error clashes
    else
        let booked = ResizeArray<Booking>()

        for occurrence in occurrences do
            match! schedulingApi.Book occurrence with
            | Ok b -> booked.Add b
            | Error _ -> () // lost a race since the probe

        return Ok(List.ofSeq booked)
}
```

This is check-then-act, not a transaction: `DetectConflicts` is a pure read, so a concurrent caller can claim a window between the probe and the booking. It gives the reader a clean refusal in the common case, not an atomicity guarantee — a deployment that needs one replaces `IBookingScheduler` with a locking implementation, as above.

## 9. Export to iCalendar

There is no `ExportICalendar` call on the API — the `iCalendar` module is a pure codec, and serving `.ics` is your module's route to write:

```fsharp
let ics (bookings: Booking list) =
    iCalendar.emit {
        Version = "2.0"
        ProdId = iCalendar.CanonicalProdId
        Events = bookings |> List.map iCalendar.bookingToVEvent
    }
```

Serve it as a download:

```fsharp
let icalRoute: HttpHandler =
    fun next ctx -> task {
        let resourceId = ctx.Request.Query["resource"].ToString()
        let scheduler = ctx.RequestServices.GetRequiredService<IBookingScheduler>()
        // Every IBookingScheduler method is scope-first and tupled.
        let! bookings = scheduler.ListBookings(scopeId, resourceId, exportWindow) |> Async.StartAsTask
        ctx.SetContentType "text/calendar"
        ctx.SetHttpHeader("Content-Disposition", "attachment; filename=bookings.ics")
        return! ctx.WriteStringAsync(ics bookings)
    }
```

`bookingToVEvent` is lossy — `Status`, `BookedBy`, `BookedFor`, `ParentBookingId` and `Metadata` have no iCal representation. That is the right trade for a calendar client that does not speak ToolUp; use `vEventToBooking` with a `defaults` booking to restore them on import.

## Next steps

- [concepts.md](concepts.md) — data model, concurrency model, recurrence semantics, iCalendar wire format.
- [api-reference.md](api-reference.md) — full public surface.
- [extending.md](extending.md) — custom `IBookingScheduler` impls, multi-resource patterns, calendar UI.

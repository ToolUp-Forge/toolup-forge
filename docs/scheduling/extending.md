# Extending ToolUp.Scheduling

How to write a custom `IBookingScheduler` impl, build multi-resource patterns, and integrate with calendar UI libraries.

## Replacing `IBookingScheduler`

The default `BookingScheduler` is single-instance (uses in-process `SemaphoreSlim`). For multi-instance deployments, a distributed-lock-backed alternative slots in:

```fsharp skip=fragment
type RedisLockedBookingScheduler(entityStore: IEntityStore, redis: IConnectionMultiplexer) =
    interface IBookingScheduler with
        member _.Book(scopeId, request, bookedBy) = async {
            let lockKey = $"booking-lock:{scopeId}:{request.ResourceId}"
            let database = redis.GetDatabase()

            // Distributed lock via Redis SET NX EX
            let lockToken = Guid.NewGuid().ToString()
            let acquired = database.StringSetAsync(
                                lockKey,
                                lockToken,
                                expiry = TimeSpan.FromSeconds 30.,
                                when_ = When.NotExists)
                            |> Async.AwaitTask
                            |> Async.RunSynchronously
            if not acquired then
                return Error (StorageError "Could not acquire booking lock — try again")
            try
                // ... rest of booking logic mirrors default impl
                let! existing = entityStore.Query<Booking> (...)
                if existsConflict existing request then
                    return Error SlotOccupied
                else
                    let booking = { ... }
                    let! _ = entityStore.Save booking
                    return Ok booking
            finally
                // Release lock — Lua-script for atomic check-and-delete
                let script = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end"
                database.ScriptEvaluateAsync(script, [|RedisKey lockKey|], [|RedisValue.op_Implicit lockToken|])
                |> Async.AwaitTask
                |> Async.RunSynchronously
                |> ignore
        }
        // ... other members
```

Wire:

```fsharp skip=fragment
ServerApp.empty
|> ...
|> ServerApp.withBookingScheduler (RedisLockedBookingScheduler(entityStore, redis) :> IBookingScheduler)
|> SchedulingServerApp.fromServerApp
|> ...
```

Run `IBookingSchedulerContract` against your impl to verify conformance.

## Multi-resource patterns

The shipped scheduler is per-`ResourceId`. For multi-resource booking (assign N customers to M practitioners), express it as:

### Pattern 1 — separate resources, parallel booking

Each practitioner is their own `Resource`. A booking targets one specific practitioner. The customer-facing UI lets them pick (or auto-assigns).

```fsharp skip=fragment
// Each practitioner is a BookableResource with their own weekly availability.
let practitioners = [ "p-1", "Alice"; "p-2", "Bob"; "p-3", "Carol" ]

// Booking targets one practitioner explicitly.
let! result = schedulingApi.Book { seedBooking with ResourceId = "p-2" }
```

For auto-assignment, the module asks each practitioner for their free slots and picks the earliest. `ResourceId` is a plain `string`, and `FindAvailableSlots` emits only free windows — there is no slot status to filter on:

```fsharp skip=fragment
let assignNextAvailable (candidates: ResourceId list) (window: DateRange) = async {
    let! perCandidate =
        candidates
        |> List.map (fun rid -> async {
            let! slots =
                schedulingApi.FindAvailableSlots {
                    ResourceId = rid
                    Window = window
                    SlotDurationMinutes = 60
                }
            return rid, List.tryHead slots
        })
        |> Async.Parallel

    return
        perCandidate
        |> Array.choose (fun (rid, slot) -> slot |> Option.map (fun s -> rid, s))
        |> Array.sortBy (fun (_, slot) -> slot.Start)
        |> Array.tryHead
}
```

### Pattern 2 — composite resource

A composite resource represents the "any available practitioner" abstraction. Implement a custom `IBookingScheduler` that routes:

```fsharp skip=fragment
type PractitionerPoolScheduler(poolResourceId: ResourceId, poolMembers: ResourceId list, entityStore: IEntityStore) =
    interface IBookingScheduler with
        // Every IBookingScheduler method is scope-first and tupled; the
        // mutating ones also take the acting user id for the audit payload.
        member this.Book(scopeId, booking, actorUserId) = async {
            if booking.ResourceId = poolResourceId then
                // Resolve to a specific pool member with capacity.
                match! this.pickAvailableMember scopeId booking with
                | Some specificId ->
                    return! this.bookSpecific scopeId { booking with ResourceId = specificId } actorUserId
                | None ->
                    // No free member: report it as a schedule disagreement, not a
                    // lookup failure, so the UI can list every reason at once.
                    return Error(Conflicts [ OverlappingBooking booking.Id ])
            else
                // Direct booking against a specific resource.
                return! this.bookSpecific scopeId booking actorUserId
        }
        // ...
```

The customer books the pool resource; the scheduler picks a free member; the booking persists against the specific member. The customer-facing UI sees "Pool 1 booked"; the back-office sees "Bob booked".

## Calendar UI integration

The SDK ships no built-in calendar component. Plug in a Feliz-compatible library:

### Pattern — wrap FullCalendar

```fsharp skip=fragment
module FullCalendarBindings

open Feliz

type ICalendarEvent =
    abstract id: string
    abstract title: string
    abstract start: string
    abstract ``end``: string
    abstract backgroundColor: string

[<ReactComponent>]
let CalendarView (events: ICalendarEvent[]) (onSlotClick: DateTime -> unit) =
    Html.div [
        prop.className "fc-wrapper"
        prop.children [
            // FullCalendar React component imported via Fable
            FullCalendar [
                FullCalendar.events events
                FullCalendar.dateClick (fun info -> onSlotClick info.date)
            ]
        ]
    ]
```

Then in your module's `ClientView.fs`:

```fsharp skip=fragment
// model.Slots    : TimeSlot list — free windows from FindAvailableSlots
// model.Bookings : Booking list  — what is already claimed in the same window
let calendarView (model: Model) (dispatch: Msg -> unit) =
    let freeEvents =
        model.Slots
        |> List.toArray
        |> Array.map (fun slot -> {|
            id = slot.Start.ToString("O")
            title = "Available"
            start = slot.Start.ToString("O")
            ``end`` = slot.End.ToString("O")
            backgroundColor = "#10b981"
        |} :> ICalendarEvent)

    let bookedEvents =
        model.Bookings
        |> List.filter (fun b -> b.Status = Confirmed || b.Status = Tentative)
        |> List.toArray
        |> Array.map (fun b -> {|
            id = b.Id
            title = b.Title
            start = b.StartUtc.ToString("O")
            ``end`` = b.EndUtc.ToString("O")
            backgroundColor = if b.Status = Tentative then "#f59e0b" else "#ef4444"
        |} :> ICalendarEvent)

    FullCalendarBindings.CalendarView
        (Array.append freeEvents bookedEvents)
        (fun date -> dispatch (BookSlot date))
```

### Other calendar libraries

- **React Big Calendar** — well-established, similar wrap pattern.
- **Toast UI Calendar** — feature-rich, more complex wrap.
- **Day.js scheduler** — lighter.

Pick what fits your aesthetic / UX requirements; the SDK doesn't lock you in.

## Two-way calendar sync (deferred extension)

A future `ICalendarSyncProvider` extension point would pull external availability:

```fsharp
type ICalendarSyncProvider =
    abstract FetchExternalEvents: resourceId: ResourceId * window: DateRange -> Async<AvailabilityException list>
```

A `GoogleCalendarSyncProvider` companion would query Google Calendar's API for the resource's owner and project what it found as dated `AvailabilityException`s — `Kind = PartialBlock` for a timed event, `FullDay` for an all-day one. `FindAvailableSlots` already subtracts those, so nothing downstream would need to change.

Currently this is a deferred extension. Build it as a custom module-side layer for now:

```fsharp skip=fragment
let slotsWithExternalSync resourceId window = async {
    let! slots =
        schedulingApi.FindAvailableSlots {
            ResourceId = resourceId
            Window = window
            SlotDurationMinutes = 60
        }
    let! externalEvents = googleCalendarApi.fetchEvents resourceId window
    return subtractExternal slots externalEvents
}
```

## Custom recurrence

The shipped `RecurrenceExpander` covers Daily / Weekly / Monthly / Yearly with `Count` / `Until` termination and a `ByWeekday` filter on Weekly rules. Sub-day frequencies and the complex monthly forms (`BySetPos`, `ByMonthDay`) are out of scope in v1. For richer recurrence — multi-modifier `BYDAY`, business days, exception dates — write a custom expander over `occurrenceStarts`:

```fsharp skip=fragment
module CustomRecurrence

let occurrencesExcept
    (seed: DateTimeOffset)
    (rule: RecurrenceRule)
    (upperBound: DateTimeOffset)
    (exceptions: DateTimeOffset list)
    : DateTimeOffset list =
    RecurrenceExpander.occurrenceStarts seed rule upperBound
    |> List.filter (fun d -> not (List.contains d exceptions))
```

Or wrap an existing RFC 5545 library:

```fsharp skip=fragment
type FullICalRecurrenceExpander(icalLib: IMyRRuleLibrary) =
    member _.Expand (rule: string) (seed: DateTimeOffset) : DateTimeOffset list =
        icalLib.ExpandRRule rule seed
```

Both expander entry points are pure, so a consumer can substitute them without changing the scheduler. There is no series-booking call to intercept: `Book` persists a seed carrying `Recurrence`, and `ExpandRecurrence` materialises occurrences on demand — so for a rule the shipped expander cannot express, expand it yourself and call `Book` per occurrence with `ParentBookingId` set.

## Wait lists

When a booking cancels, auto-promote from a wait list. Build at the module layer:

```fsharp skip=fragment
// Subscribe to BookingCancelled events
let waitListPromoter scopeId =
    eventStore.Subscribe "_platform.audit" "BookingCancelled" (fun event -> async {
        let cancelledBooking = parseBookingCancelled event
        let! waitList = readWaitListForResource cancelledBooking.ResourceId
        match waitList with
        | next :: _ ->
            let! _ = schedulingApi.Book {
                ResourceId = cancelledBooking.ResourceId
                Start = cancelledBooking.Start
                End = cancelledBooking.End
                Notes = Some $"Promoted from wait list — {next.CustomerId}"
            }
            do! markWaitListEntryFulfilled next.Id
            do! sendPromotionEmail next.Email
        | [] -> ()
    })
```

The wait-list itself is a custom entity store; the cancel-event subscription drives the promotion logic.

## Group bookings

Express N customers in one slot as N parallel resources of the same kind:

```fsharp skip=fragment
let class1Spot1 = { ResourceId = ResourceId "class-1-spot-1"; Name = "Yoga Class A — Spot 1"; ... }
let class1Spot2 = { ResourceId = ResourceId "class-1-spot-2"; Name = "Yoga Class A — Spot 2"; ... }
// ...
```

Customers book a specific spot. For "any spot available" UX, the `PractitionerPoolScheduler` pattern above generalises.

Alternatively, lift "group capacity" into a custom scheduler that tracks N concurrent bookings per resource (the shipped scheduler caps at 1 — the slot's `SemaphoreSlim` is `new SemaphoreSlim(1, 1)`).

```fsharp skip=fragment
type CapacityScheduler(capacity: int, entityStore: IEntityStore) =
    interface IBookingScheduler with
        member _.Book(scopeId, request, bookedBy) = async {
            let lock = getLock request.ResourceId
            do! lock.WaitAsync()
            try
                let! existing = entityStore.Query<Booking> (overlapsAt request)
                let concurrent = existing |> List.filter (fun b -> b.Status = Confirmed) |> List.length
                if concurrent >= capacity then
                    return Error SlotOccupied
                else
                    // Persist
                    let booking = { ... }
                    let! _ = entityStore.Save booking
                    return Ok booking
            finally
                lock.Release() |> ignore
        }
        // ...
```

This is the pattern for class bookings (10 students per class), shared-resource bookings (4 parking spaces per garage), etc.

## Companion conventions

Most scheduling extensions live in your own module code, not in companion packages. The interfaces (`IBookingScheduler`, `ICalendarSyncProvider`) are stable; the wire format is committed. For deeper customisation:

- Replace `IBookingScheduler` outright for distributed-lock / capacity / pool semantics.
- Wrap with decorators for wait-list / sync / multi-resource composition.
- Custom Feliz components for the calendar grid UI.

The shipped scheduler is intentionally narrow — single-resource concurrency-safe booking with recurrence. Most real apps build a thin domain layer on top.

# Extending ToolUp.Scheduling

How to write a custom `IBookingScheduler` impl, build multi-resource patterns, and integrate with calendar UI libraries.

## Replacing `IBookingScheduler`

The default `BookingScheduler` is single-instance (uses in-process `SemaphoreSlim`). For multi-instance deployments, a distributed-lock-backed alternative slots in:

```fsharp
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

```fsharp
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

```fsharp
let practitioner1 = { ResourceId = ResourceId "p-1"; Name = "Alice"; ... }
let practitioner2 = { ResourceId = ResourceId "p-2"; Name = "Bob"; ... }
let practitioner3 = { ResourceId = ResourceId "p-3"; Name = "Carol"; ... }

// Booking targets one practitioner explicitly
let! result = schedulingApi.Book {
    ResourceId = ResourceId "p-2"
    Start = ...
    End = ...
    Notes = ...
}
```

For auto-assignment, the module queries each practitioner's slots and picks the one with the earliest available slot:

```fsharp
let assignNextAvailable (services: PractitionerId list) (preferredDate: DateTime) = async {
    let! candidateSlots =
        services
        |> List.map (fun pid -> async {
            let! slots =
                schedulingApi.ListSlots {
                    ResourceId = pid
                    Start = preferredDate
                    End = preferredDate.AddDays 7.
                }
            return pid, slots |> List.tryFind (fun s -> s.Status = Free)
        })
        |> Async.Parallel

    return
        candidateSlots
        |> Array.choose (fun (pid, slotOpt) -> slotOpt |> Option.map (fun s -> pid, s))
        |> Array.sortBy (fun (_, slot) -> slot.Start)
        |> Array.tryHead
}
```

### Pattern 2 — composite resource

A composite resource represents the "any available practitioner" abstraction. Implement a custom `IBookingScheduler` that routes:

```fsharp
type PractitionerPoolScheduler(poolResourceId: ResourceId, poolMembers: ResourceId list, entityStore: IEntityStore) =
    interface IBookingScheduler with
        member this.Book(scopeId, request, bookedBy) = async {
            if request.ResourceId = poolResourceId then
                // Resolve to a specific pool member with capacity
                let! member_ = this.pickAvailableMember scopeId request
                match member_ with
                | Some specificId ->
                    return! this.bookSpecific scopeId { request with ResourceId = specificId } bookedBy
                | None ->
                    return Error SlotOccupied
            else
                // Direct booking against a specific resource
                return! this.bookSpecific scopeId request bookedBy
        }
        // ...
```

The customer books the pool resource; the scheduler picks a free member; the booking persists against the specific member. The customer-facing UI sees "Pool 1 booked"; the back-office sees "Bob booked".

## Calendar UI integration

The SDK ships no built-in calendar component. Plug in a Feliz-compatible library:

### Pattern — wrap FullCalendar

```fsharp
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

```fsharp
let calendarView (model: Model) (dispatch: Msg -> unit) =
    let calendarEvents =
        model.Slots
        |> List.toArray
        |> Array.map (fun slot -> {|
            id = slot.Start.ToString("O")
            title = slotTitle slot
            start = slot.Start.ToString("O")
            ``end`` = slot.End.ToString("O")
            backgroundColor =
                match slot.Status with
                | Free -> "#10b981"
                | Booked _ -> "#ef4444"
                | Blocked _ -> "#9ca3af"
        |} :> ICalendarEvent)
    FullCalendarBindings.CalendarView calendarEvents (fun date -> dispatch (BookSlot date))
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
    abstract FetchExternalEvents: resourceId: ResourceId -> start: DateTime -> end_: DateTime -> Async<BlockedTime list>
```

A `GoogleCalendarSyncProvider` companion would query Google Calendar's API for the resource's owner; the resulting events overlay as `BlockedTime`s in `ListSlots`. The `IBookingScheduler` would query the provider before computing slot status.

Currently this is a deferred extension. Build it as a custom module-side layer for now:

```fsharp
let listSlotsWithExternalSync resourceId start end_ = async {
    let! slots = schedulingApi.ListSlots { ResourceId = resourceId; Start = start; End = end_ }
    let! externalEvents = googleCalendarApi.fetchEvents resourceId start end_
    return overlayExternal slots externalEvents
}
```

## Custom recurrence

The shipped `RecurrenceExpander` covers Daily / Weekly / Monthly / Yearly with `Count` / `Until` termination + `ByDayOfWeek` / `ByDayOfMonth`. For richer recurrence (multi-modifier `BYDAY`, business days, exception dates), write a custom expander:

```fsharp
module CustomRecurrence

let expandWithExceptions
    (rule: RecurrenceRule)
    (startDate: DateTime)
    (exceptions: DateTime list)
    : DateTime list =
    RecurrenceExpander.expand rule startDate
    |> List.filter (fun d -> not (List.contains d exceptions))
```

Or wrap an existing RFC 5545 library:

```fsharp
type FullICalRecurrenceExpander(icalLib: ICalRecurrenceLibrary) =
    member _.expand (rule: string) (startDate: DateTime) : DateTime list =
        icalLib.expandRRule rule startDate
```

`RecurrenceExpander.expand` is a pure function; consumers can substitute it without changing the scheduler. The scheduler's `BookSeries` accepts a `RecurrenceRule` and calls the shipped expander internally — for richer rules, expand client-side and call `Book` per-date instead of `BookSeries`.

## Wait lists

When a booking cancels, auto-promote from a wait list. Build at the module layer:

```fsharp
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

```fsharp
let class1Spot1 = { ResourceId = ResourceId "class-1-spot-1"; Name = "Yoga Class A — Spot 1"; ... }
let class1Spot2 = { ResourceId = ResourceId "class-1-spot-2"; Name = "Yoga Class A — Spot 2"; ... }
// ...
```

Customers book a specific spot. For "any spot available" UX, the `PractitionerPoolScheduler` pattern above generalises.

Alternatively, lift "group capacity" into a custom scheduler that tracks N concurrent bookings per resource (the shipped scheduler caps at 1 — the slot's `SemaphoreSlim` is `new SemaphoreSlim(1, 1)`).

```fsharp
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

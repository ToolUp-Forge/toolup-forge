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
                return Error (StorageFailure "Could not acquire booking lock — try again")
            try
                // ... rest of booking logic mirrors default impl
                let! existing = entityStore.Query<Booking> (...)
                match conflictsWith existing request with
                | [] ->
                    let booking = { ... }
                    let! _ = entityStore.Save booking
                    return Ok booking
                | conflicts ->
                    // `Conflicts` is the schedule-disagreement case — it
                    // carries the whole list so the UI can surface every
                    // reason at once.
                    return Error (Conflicts conflicts)
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
// The scheduler is a DI registration: the API handler resolves
// `IBookingScheduler` out of `RequestServices` per request.
services.AddSingleton<IBookingScheduler>(redisLockedScheduler)

SchedulingServerApp.create ()
|> SchedulingServerApp.withConfig config
|> SchedulingServerApp.run
```

Run `IBookingSchedulerContract` against your impl to verify conformance.

## Multi-resource patterns

The shipped scheduler is per-`ResourceId`. For multi-resource booking (assign N customers to M practitioners), express it as:

### Pattern 1 — separate resources, parallel booking

Each practitioner is their own `Resource`. A booking targets one specific practitioner. The customer-facing UI lets them pick (or auto-assigns).

```fsharp
// Each practitioner is a BookableResource with their own weekly availability.
let practitioners = [ "p-1", "Alice"; "p-2", "Bob"; "p-3", "Carol" ]

// Booking targets one practitioner explicitly. `ResourceId` is a plain
// `string` alias, so the id needs no constructor.
let bookWithBob = async {
    let! result = schedulingApi.Book { seedBooking with ResourceId = "p-2" }
    return result
}
```

For auto-assignment, the module asks each practitioner for their free slots and picks the earliest. `ResourceId` is a plain `string`, and `FindAvailableSlots` emits only free windows — there is no slot status to filter on:

```fsharp
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

## Two-way calendar sync

Shipped since Phase 20a: bookings on a linked resource mirror outward into an external calendar, and edits made there are pulled back under a per-link conflict policy. Two pieces, split so a provider is a small adapter:

- **`ICalendarBridge`** (`ToolUp.Scheduling.Core`) — the portable surface one provider implements: `Kind`, `Capabilities` (webhooks? a modification cursor? the poll floor?), `LinkResource` / `UnlinkResource`, `Push`, `Pull`, `HandleWebhook`. A bridge holds no state and owns no store; credentials come from `ISecretStore` per call, keyed by the link's scope.
- **`CalendarSync`** (`ToolUp.Scheduling.Server`) — everything provider-independent: the resource ↔ external-calendar link store, the push job fired on `BookingCreated` / `BookingRescheduled` / `BookingCancelled`, the pull (a cron poll for polling-only bridges, a provider notification for webhook-capable ones), and the conflict policy (`ExternalWins` / `LocalWins` / `LatestModifiedWins`).

External events come back as **bookings** on the linked resource, so they block slots through the same conflict detector every booking does — nothing downstream of `IBookingScheduler` changes.

Composition is one call per provider; a deployment that never makes it registers no link entities, builds no sync engine and schedules no job (GP 13):

```fsharp
let withMirroring (app: SchedulingServerApp) (bridge: ToolUp.Scheduling.ICalendarBridge.ICalendarBridge) =
    app
    |> SchedulingServerApp.withConfig config
    |> SchedulingServerApp.withCalendarBridge bridge
```

A resource is then linked through the sync engine, naming the provider by its `Kind`, the external calendar, the user whose credentials authorise the mirror, and the conflict policy:

```fsharp
let linkRoom (sync: ToolUp.Scheduling.CalendarSync.ICalendarSync) = async {
    let! linked =
        sync.LinkResource(
            "team-a",
            "room-101",
            "Google",
            "rooms@group.calendar.google.com",
            "alice",
            ToolUp.Scheduling.ICalendarBridge.LatestModifiedWins,
            ToolUp.Platform.EntityTypes.EntityPrincipal.ofPrincipal "scheduling-admin"
        )

    return linked
}
```

Shipped providers:

| Package | Provider | Shape |
|---|---|---|
| `ToolUp.Calendar.CalDAV` | any RFC 4791 server (Fastmail, Nextcloud, iCloud, Radicale) | polling; `since` bounds event time |
| `ToolUp.Calendar.Google` | Google Calendar v3 | watch-channel notifications with scheduled renewal and a polling fallback; incremental pull on sync tokens |

**Writing a bridge for another provider.** Implement the six members, declare `Capabilities` honestly (the sync engine wires polling, notifications and the pull cursor from them), classify failures into `BridgeError` without retrying (the engine's jobs own retry), and bind the `ICalendarBridgeContract` pack from `src/ToolUp.Scheduling.Tests/Contracts/` over a stub transport — the pack holds every provider to the same round-trip, idempotency and conflict-resolution laws, and it splits the `since` law by the capability you declare. The two companion READMEs are the worked examples.

Two limits belong to the engine rather than to any bridge: an external deletion is not applied locally (cancel through the deployment, which pushes the deletion), and an external edit that changed more than the times records `BookingCreated`, because `IBookingScheduler` has no general update verb.

## Custom recurrence

The shipped `RecurrenceExpander` covers Daily / Weekly / Monthly / Yearly with `Count` / `Until` termination and a `ByWeekday` filter on Weekly rules. Sub-day frequencies and the complex monthly forms (`BySetPos`, `ByMonthDay`) are out of scope in v1. For richer recurrence — multi-modifier `BYDAY`, business days, exception dates — write a custom expander over `occurrenceStarts`:

```fsharp
module CustomRecurrence =

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

```fsharp
type FullICalRecurrenceExpander(icalLib: IMyRRuleLibrary) =
    member _.Expand (rule: string) (seed: DateTimeOffset) : DateTimeOffset list =
        icalLib.ExpandRRule rule seed
```

Both expander entry points are pure, so a consumer can substitute them without changing the scheduler. There is no series-booking call to intercept: `Book` persists a seed carrying `Recurrence`, and `ExpandRecurrence` materialises occurrences on demand — so for a rule the shipped expander cannot express, expand it yourself and call `Book` per occurrence with `ParentBookingId` set.

## Wait lists

When a booking cancels, auto-promote from a wait list. Build at the module layer.

`IEventStore` has no subscribe method — it is a write-and-query journal (`Write` / `ReadAll` / `ReadByType` / `ReadBySource`). Reacting to an event is the job substrate's `Trigger.OnEvent`, which fires a registered `IJobHandler` whenever a `ModuleEvent` of that type is written in the same scope:

```fsharp
type WaitListPromoter(schedulingApi: ISchedulingApi) =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) = async {
            // `BookingCancelledPayload` carries the booking id, not the
            // resource — read the booking back to learn what freed up.
            let cancelled = parseBookingCancelled ctx.Payload
            let! booking = schedulingApi.GetBooking cancelled.BookingId

            match booking with
            | None -> return JobResult.Success
            | Some released ->
                let! waitList = readWaitListForResource released.ResourceId

                match waitList with
                | next :: _ ->
                    let! _ =
                        schedulingApi.Book {
                            released with
                                Id = Guid.NewGuid().ToString()
                                Status = Confirmed
                                BookedFor = Some next.CustomerId
                                Title = $"Promoted from wait list — {next.CustomerId}"
                        }

                    do! markWaitListEntryFulfilled next.Id
                    do! sendPromotionEmail next.Email
                    return JobResult.Success
                | [] -> return JobResult.Success
        }
```

Register it against the event the scheduler writes — `SchedulingEvents.BookingCancelled`, under `SourceModule = "_scheduling"`:

```fsharp
SchedulingServerApp.create ()
|> SchedulingServerApp.withJobHandler (
    "wait-list-promoter",
    WaitListPromoter(schedulingApi) :> IJobHandler,
    OnEvent SchedulingEvents.BookingCancelled
)
|> SchedulingServerApp.run
```

The wait-list itself is a custom entity store; the `OnEvent` job drives the promotion logic.

## Group bookings

Express N customers in one slot as N parallel resources of the same kind:

```fsharp
// `ResourceId` is a `string` alias, so a spot id is just a string. The
// constant `Type` field is the entity-store discriminator — the
// companion publishes it as `BookingScheduler.ResourceTypeName` —
// while `ResourceType` is the caller-defined classification.
let classSpot (n: int) : BookableResource = {
    Id = sprintf "class-1-spot-%d" n
    Type = BookingScheduler.ResourceTypeName
    Version = 0
    ResourceType = "ClassSpot"
    DisplayName = sprintf "Yoga Class A — Spot %d" n
    Timezone = "Europe/London"
    DefaultAvailability = classWindows
    Metadata = Map.ofList [ "class", "yoga-a" ]
}

let class1Spots = [ for n in 1..10 -> classSpot n ]
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
                    // Over capacity is a schedule disagreement, so it
                    // rides `BookingError.Conflicts`.
                    return Error (Conflicts [ OverlappingBooking request.Id ])
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

Most scheduling extensions live in your own module code, not in companion packages. The shipped interface (`IBookingScheduler`) is stable and the `ISchedulingApi` wire format is committed; calendar providers are the one extension that IS a companion package, behind the shipped `ICalendarBridge` seam. For deeper customisation:

- Replace `IBookingScheduler` outright for distributed-lock / capacity / pool semantics.
- Wrap with decorators for wait-list / sync / multi-resource composition.
- Custom Feliz components for the calendar grid UI.

The shipped scheduler is intentionally narrow — single-resource concurrency-safe booking with recurrence. Most real apps build a thin domain layer on top.

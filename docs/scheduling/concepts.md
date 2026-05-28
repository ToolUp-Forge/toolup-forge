# Concepts

How `ToolUp.Scheduling` works under the hood.

## Data model

Three primary entities:

### `Resource`

One bookable thing. Carries its own availability + slot configuration.

```fsharp
type Resource = {
    ResourceId: ResourceId
    Name: string
    Description: string option
    AvailabilityWindows: AvailabilityWindow list
    BlockedTimes: BlockedTime list
    SlotDurationMinutes: int
    BufferBetweenSlotsMinutes: int
}

and AvailabilityWindow = {
    Days: DayOfWeek list             // recurring weekly
    StartTime: TimeSpan
    EndTime: TimeSpan
    Timezone: string                  // IANA tz name (e.g. "Europe/London")
}

and BlockedTime = {
    Start: DateTime
    End: DateTime
    Reason: string
}
```

Persisted as a `IEntityStore` entity. Multi-tenant deployments scope by team; a resource lives in one team's container.

### `Slot`

A computed time interval against a resource. Slots aren't persisted — they're derived per query from `AvailabilityWindows` + `BlockedTimes` + existing `Booking`s.

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

### `Booking`

A claim against a slot. Persisted.

```fsharp
type Booking = {
    BookingId: BookingId
    ResourceId: ResourceId
    Start: DateTime
    End: DateTime
    BookedBy: string
    BookedAt: DateTime
    Notes: string option
    Status: BookingStatus
    SeriesId: SeriesId option        // populated for series bookings
}

and BookingStatus =
    | Confirmed
    | Cancelled of cancelledAt: DateTime
```

Indexed by `ResourceId` + compound `(ResourceId, Start)` for fast range queries.

## Slot derivation

`ListSlots` is a pure function over the resource definition + existing bookings:

```fsharp
let listSlots (resource: Resource) (bookings: Booking list) (start: DateTime) (end_: DateTime) : Slot list =
    let candidateSlots = enumerateSlots resource start end_
    candidateSlots
    |> List.map (fun slot ->
        let status =
            // Check blocked times first
            match resource.BlockedTimes |> List.tryFind (overlaps slot) with
            | Some blocked -> Blocked blocked.Reason
            | None ->
                // Then existing bookings
                match bookings |> List.tryFind (fun b -> b.Status = Confirmed && overlaps slot b) with
                | Some booking -> Booked booking.BookingId
                | None -> Free
        { slot with Status = status })
```

`enumerateSlots` walks `AvailabilityWindow` × `SlotDuration` × `Buffer`, applying timezone conversion at each step. Daylight-saving handled — UTC stored, local rendered.

Result: deterministic slot grid against the resource definition; no race condition between `ListSlots` and `Book` because slots are derived, not persisted.

## Concurrency model

`Book` uses a `SemaphoreSlim` per `ResourceId`:

```fsharp
let private locks = ConcurrentDictionary<ResourceId, SemaphoreSlim>()

let private getLock (resourceId: ResourceId) =
    locks.GetOrAdd(resourceId, fun _ -> new SemaphoreSlim(1, 1))

let book (request: BookingRequest) : Async<Result<Booking, BookingError>> = async {
    let lock = getLock request.ResourceId
    do! lock.WaitAsync() |> Async.AwaitTask
    try
        // 1. Read existing bookings for the resource in the request's time range
        let! existing = entityStore.Query<Booking> (...)

        // 2. Validate slot is Free
        if existsConflict existing request then
            return Error SlotOccupied
        else
            // 3. Persist new booking
            let booking = { BookingId = newGuid(); ... }
            let! _ = entityStore.Save booking
            return Ok booking
    finally
        lock.Release() |> ignore
}
```

Per-resource lock means:
- Concurrent bookings against **different** resources don't contend (high throughput).
- Concurrent bookings against the **same** resource serialise (no double-booking).

The lock is in-process. For multi-instance deployments, a distributed lock (Redis, etcd, or `IDistributedLock` companion) replaces it. Currently single-instance only; multi-instance is a future extension.

`SemaphoreSlim` instances live for the process lifetime; not GC'd as resources come and go. For deployments with thousands of resources cycling per day, the leak is real but slow (`SemaphoreSlim` is ~80 bytes). For typical deployments, fine.

## Recurrence expansion

`RecurrenceRule` is RFC 5545-inspired but simplified — sufficient for common booking patterns.

```fsharp
type RecurrenceRule = {
    Frequency: Frequency
    Interval: int
    ByDayOfWeek: DayOfWeek list
    ByDayOfMonth: int list
    Count: int option
    Until: DateTime option
}

and Frequency = Daily | Weekly | Monthly | Yearly
```

`RecurrenceExpander.expand` is pure:

```fsharp
let expand (rule: RecurrenceRule) (startDate: DateTime) : DateTime list =
    // ...
```

Termination:
- `Count = Some N` — exactly N occurrences.
- `Until = Some date` — occurrences up to and including the date.
- Both — whichever ends sooner.
- Neither — error at validation; rules must terminate.

### What's NOT supported

- Multiple `BYDAY` modifiers like RFC 5545's `1MO` (first Monday of month). The `ByDayOfWeek` field is unmodified — every match.
- Exception dates (`EXDATE`). Use one-off `Cancel` after series-book.
- Complex business-day rules (work-week-only, exclude bank holidays). Either:
  - Use `BlockedTime`s to overlay holidays per resource.
  - Or filter the expanded dates client-side before book-series.

For richer recurrence, an `IRecurrenceProvider` extension point (deferred) would slot in a full RFC 5545 expander.

## iCalendar export

```fsharp
let! ics = schedulingApi.ExportICalendar resourceId
// ics : string (RFC 5545-compliant .ics content)
```

Output format:

```
BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//ToolUp//Scheduling//EN
BEGIN:VEVENT
UID:booking-{bookingId}@toolup
DTSTAMP:20260512T140000Z
DTSTART:20260512T140000Z
DTEND:20260512T150000Z
SUMMARY:{booking notes or resource name}
END:VEVENT
END:VCALENDAR
```

Consumers (Google Calendar, Outlook, Apple Calendar) subscribe via `webcal://...` URLs. The endpoint can be served from a public URL with token-gated access — per-customer "your calendar" subscriptions are a common pattern.

Cancelled bookings are excluded from the export. Series bookings are exported as individual events (not as `RRULE`); the calendar shows N separate events, which is what most consumers want.

## Audit + observability

Three audit events under `_platform.audit`:
- `BookingCreated` — new booking confirmed.
- `BookingCancelled` — booking cancelled (caller must own the booking or be team admin).
- `ResourceUpdated` — resource definition changed (admin-only).

Each event carries actor, resource id, booking id, server-side timestamp. Replicated by audit-sink subsystem for compliance trails.

`SchedulingHealth` `IHealthCheck` probe verifies the scheduler can read from `IEntityStore`; self-registered via DI.

## Scope isolation

Resources are persisted in `team-{teamId}` containers (in `Team` / `MultiTeam` modes) or `user-{userId}` (in `Individual` mode). Bookings inherit the resource's scope. Team A's resources and bookings are never visible to Team B's callers.

In `Anonymous` / `AuthenticatedEphemeral` modes, scheduling works but data doesn't persist beyond the session — useful for dev / demo but not production.

## Performance

- **`ListSlots` cost**: O(slots_in_range × bookings_in_range). Bookings indexed on `(ResourceId, Start)` so the range query is fast. Single-resource one-week queries return in <50ms even with thousands of bookings.
- **`Book` cost**: one entity-store read (for conflict check) + one write. Bounded by the lock holding time — typically <10ms per booking.
- **`BookSeries` cost**: one entity-store read (range query for the series window) + N writes. Atomic across the writes via the lock.

For high-frequency booking (real-time bidding, ticketing platforms), the per-resource lock would bottleneck — distributed-lock companions are the migration path.

## What scheduling does NOT cover

- **Notifications** — sending email confirmations, SMS reminders. Use `INotificationSink` (Phase 6f) — wire a workflow action via `ToolUp.Forms` if you want the form-driven shape, or call the sink directly from your module.
- **Payment** — collecting deposits, processing refunds on cancel. Out of scope; integrate Stripe / payment provider at the module layer.
- **Customer notes** — bookings have `Notes: string option`, not a full CRM record. For customer history, use a custom entity store via `IEntityStore` directly.
- **Two-way calendar sync** — pulling availability from Google Calendar / Outlook in real-time so external events block slots. Out of scope; future companion work (`ICalendarSyncProvider`).
- **Group bookings** — N customers in one slot. Express via N parallel `Resource`s of the same kind ("Class A1", "Class A2", ...).
- **Wait lists** — when a booked customer cancels, auto-promote from a wait list. Out of scope; build as a custom module emitting a `Cancellation` event that subscribers handle.

For any of these, the right shape is a custom module on top of `IBookingScheduler`. The scheduler covers the low-level concurrency-safe booking primitive; richer flows compose on top.

## Six-rule portability audit

`IBookingScheduler` satisfies the six portability rules:
- Identity by value — `ResourceId` / `BookingId` are strings.
- Async at every boundary.
- No callback / supervision hooks.
- Stateless between invocations.
- Per-resource ordering (rule 5 — single-resource ordering preserved).
- Precision floor: minute. Sub-minute booking is rejected at validation.

Conformance: `IBookingSchedulerContract` test pack covers the booking + concurrency + conflict semantics. Drop-in alternatives validate against the same pack.

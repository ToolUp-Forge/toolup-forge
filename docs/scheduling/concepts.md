# Concepts

How `ToolUp.Scheduling` works under the hood.

## Data model

Three primary entities:

### `Resource`

One bookable thing. Carries its own availability + slot configuration.

```fsharp
type BookableResource = {
    Id: ResourceId
    Type: string                      // entity-store discriminator
    Version: int
    ResourceType: string              // "Person" / "Room" / "Equipment" / caller-defined
    DisplayName: string
    Timezone: string                  // IANA tz name (e.g. "Europe/London")
    DefaultAvailability: AvailabilityWindow list
    Metadata: Map<string, string>
}

and AvailabilityWindow = {
    DayOfWeek: DayOfWeek option       // None = applies every day
    StartTime: TimeOnly               // local, inclusive
    EndTime: TimeOnly                 // local, exclusive; <= StartTime wraps midnight
    EffectiveFrom: DateOnly option
    EffectiveTo: DateOnly option
}
```

Persisted as a `IEntityStore` entity — hence the `Type` / `Version` fields. Multi-tenant deployments scope by team; a resource lives in one team's container.

One-off deviations from the weekly pattern are separate dated entities rather than a list on the resource, so a blocked afternoon and an opened Saturday are the same shape:

```fsharp
type AvailabilityException = {
    Id: string
    Type: string
    Version: int
    ResourceId: ResourceId
    Date: DateOnly
    Kind: ExceptionKind
    StartTime: TimeOnly option        // required for PartialBlock / ExtendedHours
    EndTime: TimeOnly option
    Reason: string option
}

and ExceptionKind =
    | FullDay        // unavailable all day; StartTime / EndTime ignored
    | PartialBlock   // unavailable during a window inside the day
    | ExtendedHours  // available OUTSIDE the default windows
```

### `TimeSlot`

A computed time interval against a resource. Slots aren't persisted — they're derived per query by `FindAvailableSlots` from `DefaultAvailability` + `AvailabilityException`s + existing `Booking`s. A slot carries no status: only free windows are emitted.

```fsharp
type TimeSlot = {
    Start: DateTimeOffset
    End: DateTimeOffset
    ResourceId: ResourceId
}
```

### `Booking`

A claim against a slot. Persisted.

```fsharp
type Booking = {
    Id: BookingId
    Type: string
    Version: int
    ResourceId: ResourceId
    Title: string
    StartUtc: DateTimeOffset
    EndUtc: DateTimeOffset
    Status: BookingStatus
    BookedBy: string                  // principal who created the booking
    BookedFor: string option          // when an admin books on someone's behalf
    Recurrence: RecurrenceRule option // Some = this is a series seed
    ParentBookingId: BookingId option // Some = an occurrence override of a series
    Metadata: Map<string, string>
}

and BookingStatus =
    | Tentative   // soft reservation; still counts for conflict detection
    | Confirmed   // hard reservation
    | Cancelled   // ignored by conflict detection
    | NoShow      // retained for audit, ignored by future conflict detection
```

There is no series identifier: a series is a seed booking carrying `Recurrence`, and an occurrence override points back at it through `ParentBookingId`.

## Slot derivation

`FindAvailableSlots` derives conflict-free windows rather than a graded slot grid. It walks the resource's default availability windows, subtracts existing bookings (ignoring `Cancelled` and `NoShow`), subtracts `FullDay` / `PartialBlock` exceptions, adds `ExtendedHours` exceptions, and emits slots of the requested duration on whatever boundaries survive:

```fsharp skip=signature
abstract FindAvailableSlots:
    scopeId: string * resourceId: ResourceId * window: DateRange * slotDurationMinutes: int -> Async<TimeSlot list>
```

Slot length is therefore a property of the *query*, not of the resource. Times are stored as `DateTimeOffset` in UTC and lifted against `BookableResource.Timezone` for the local-window comparison, so daylight saving is handled at evaluation time.

Result: no race condition between listing and booking, because slots are derived rather than persisted — and `Book` re-runs conflict detection before it writes regardless.

## Concurrency model

`Book` uses a `SemaphoreSlim` per `ResourceId`:

```fsharp skip=fragment
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

```fsharp skip=fragment
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

```fsharp skip=fragment
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

- **Notifications** — sending email confirmations, SMS reminders. Use `INotificationSink` — wire a workflow action via `ToolUp.Forms` if you want the form-driven shape, or call the sink directly from your module.
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

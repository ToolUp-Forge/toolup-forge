# API reference

Public surface of `ToolUp.Scheduling`.

## `ToolUp.Scheduling.Core`

### Identity types

```fsharp
type ResourceId = ResourceId of string
type BookingId = BookingId of string
type SeriesId = SeriesId of string
```

### `Resource`

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
    Days: DayOfWeek list
    StartTime: TimeSpan
    EndTime: TimeSpan
    Timezone: string                 // IANA tz name
}

and BlockedTime = {
    Start: DateTime
    End: DateTime
    Reason: string
}
```

### `Slot`

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
    SeriesId: SeriesId option
}

and BookingStatus =
    | Confirmed
    | Cancelled of cancelledAt: DateTime
```

### `RecurrenceRule`

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

### `BookingError`

```fsharp
type BookingError =
    | OutsideAvailability
    | SlotOccupied
    | ResourceNotFound
    | Forbidden
    | InvalidTimeRange of reason: string
    | StorageError of reason: string
```

### `ISchedulingApi`

```fsharp
type ISchedulingApi = {
    ListResources: unit -> Async<Resource list>
    GetResource: ResourceId -> Async<Resource option>
    SaveResource: Resource -> Async<Result<Resource, BookingError>>
    DeleteResource: ResourceId -> Async<Result<unit, BookingError>>

    ListSlots: ListSlotsRequest -> Async<Slot list>

    Book: BookingRequest -> Async<Result<Booking, BookingError>>
    BookSeries: BookSeriesRequest -> Async<Result<Booking list, BookingError * DateTime list>>
    Cancel: BookingId -> Async<Result<unit, BookingError>>
    GetBooking: BookingId -> Async<Booking option>
    ListBookings: ListBookingsRequest -> Async<Booking list>

    ExportICalendar: ResourceId -> Async<string>
}

and ListSlotsRequest = {
    ResourceId: ResourceId
    Start: DateTime
    End: DateTime
}

and BookingRequest = {
    ResourceId: ResourceId
    Start: DateTime
    End: DateTime
    Notes: string option
}

and BookSeriesRequest = {
    ResourceId: ResourceId
    DurationMinutes: int
    Recurrence: RecurrenceRule
    StartDate: DateTime
    StartTime: TimeSpan
    Notes: string option
}

and ListBookingsRequest = {
    ResourceId: ResourceId option
    Status: BookingStatus option
    Start: DateTime option
    End: DateTime option
    LimitTo: int option
}
```

### `RecurrenceExpander`

```fsharp skip=signature
module RecurrenceExpander =
    val expand: rule: RecurrenceRule -> startDate: DateTime -> DateTime list
    val validate: rule: RecurrenceRule -> Result<unit, RecurrenceError>

and RecurrenceError =
    | UnterminatedRecurrence         // neither Count nor Until set
    | ConflictingTermination         // both Count and Until set inconsistently
    | InvalidInterval of int          // Interval <= 0
```

### `iCalendar`

```fsharp skip=signature
module iCalendar =
    val toICalString: events: ICalEvent list -> string
    val fromBooking: booking: Booking -> resource: Resource -> ICalEvent

and ICalEvent = {
    Uid: string
    DtStart: DateTime               // UTC
    DtEnd: DateTime                 // UTC
    Summary: string
    Description: string option
    Created: DateTime
}
```

`toICalString` produces RFC 5545–compliant content. `fromBooking` is the standard mapping.

## `ToolUp.Scheduling.Server`

### `IBookingScheduler`

```fsharp
type IBookingScheduler =
    abstract ListResources: scopeId: string -> Async<Resource list>
    abstract GetResource: scopeId: string -> resourceId: ResourceId -> Async<Resource option>
    abstract SaveResource: scopeId: string -> resource: Resource -> Async<Result<Resource, BookingError>>
    abstract DeleteResource: scopeId: string -> resourceId: ResourceId -> Async<Result<unit, BookingError>>

    abstract ListSlots: scopeId: string -> request: ListSlotsRequest -> Async<Slot list>

    abstract Book: scopeId: string -> request: BookingRequest -> bookedBy: string -> Async<Result<Booking, BookingError>>
    abstract BookSeries: scopeId: string -> request: BookSeriesRequest -> bookedBy: string -> Async<Result<Booking list, BookingError * DateTime list>>
    abstract Cancel: scopeId: string -> bookingId: BookingId -> cancelledBy: string -> Async<Result<unit, BookingError>>
    abstract GetBooking: scopeId: string -> bookingId: BookingId -> Async<Booking option>
    abstract ListBookings: scopeId: string -> request: ListBookingsRequest -> Async<Booking list>

    abstract ExportICalendar: scopeId: string -> resourceId: ResourceId -> Async<string>
```

Default impl: `BookingScheduler` over `IEntityStore`. Per-`ResourceId` `SemaphoreSlim` for concurrency.

### `BookingConflictDetector`

```fsharp skip=signature
module BookingConflictDetector =
    val findConflicts:
        existing: Booking list ->
        candidate: BookingRequest ->
        Booking list
```

Pure function for conflict detection. Used internally by `Book`; exposed for client-side preview.

### `SchedulingServerApp`

```fsharp skip=signature
type SchedulingServerApp = {
    Server: ServerApp
    Resources: Resource list
}

module SchedulingServerApp =
    val fromServerApp: ServerApp -> SchedulingServerApp
    val withResource: Resource -> SchedulingServerApp -> SchedulingServerApp
    val run: SchedulingServerApp -> int
```

`withResource` registers a compose-time resource (persisted at startup to the platform scope; per-team resources persist via `SaveResource` at runtime).

## `ToolUp.Scheduling.Client`

The client surface is small — no built-in calendar UI. The shipped pieces:

### `SchedulingClient.proxy`

```fsharp skip=signature
val proxy: ISchedulingApi
```

ToolUp.Remoting proxy. Use in Elmish commands:

```fsharp
Cmd.OfAsync.either (fun () -> SchedulingClient.proxy.Book request) () onSuccess onFailure
```

### `RecurrenceFormFields` (Feliz components)

```fsharp skip=fragment
RecurrenceFormFields.render
    {| Rule: RecurrenceRule
       OnChange: RecurrenceRule -> unit |}
```

Renders the form inputs for editing a `RecurrenceRule` (frequency picker, day-of-week multiselect, count / until selector). Drop into any module that needs recurrence editing.

## Events emitted to `IEventStore`

Under `_platform.audit`:
- `ResourceCreated`
- `ResourceUpdated`
- `ResourceDeleted`
- `BookingCreated`
- `BookingCancelled`
- `BookingSeriesCreated` (covers all bookings in a series; carries `SeriesId`)

## HTTP endpoints

Auto-injected by `SchedulingServerApp.run`:

- `POST /api/ISchedulingApi/*` — every method on the interface.
- Optional: `GET /api/scheduling/{resourceId}/calendar.ics` — for direct iCalendar subscription (set up manually by your module; the SDK doesn't auto-inject this URL).

## Configuration knobs

- `ServerConfig.EntityStore = EnabledEntityStore` — required.
- `SchedulingServerApp.Resources` (the compose-time resource list) — propagated to the storage layer at startup.

## Conformance test pack

`ToolUp.Scheduling.Tests` ships:
- `IBookingSchedulerContract` — N tests covering CRUD on resources, slot derivation, booking with concurrency, conflict detection, series booking atomicity.
- `RecurrenceExpanderTests` — covers Daily / Weekly / Monthly / Yearly + Count + Until terminations + edge cases (DST transitions, leap year, year boundary).

External impls bind into the contract pack:

```fsharp
[<Tests>]
let tests =
    testList "MyBookingScheduler conformance" [
        yield! IBookingSchedulerContract.tests
            (fun () -> MyBookingScheduler.create() :> IBookingScheduler)
    ]
```

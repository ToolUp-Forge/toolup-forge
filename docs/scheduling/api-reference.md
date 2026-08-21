# API reference

Public surface of `ToolUp.Scheduling`.

## `ToolUp.Scheduling.Core`

### Identity types

Both are plain `string` aliases, so natural-key domains (employee numbers, room codes) flow through unchanged.

```fsharp
type ResourceId = string
type BookingId = string
```

There is no series identifier: a recurring series is a seed `Booking` carrying `Recurrence = Some rule`, and an occurrence override points back at the seed through `ParentBookingId`.

### `BookableResource`

```fsharp
type BookableResource = {
    Id: ResourceId
    Type: string                 // entity-store discriminator (constant)
    Version: int                 // assigned by the store on save
    ResourceType: string         // "Person" / "Room" / "Equipment" / caller-defined
    DisplayName: string
    Timezone: string             // IANA tz name, e.g. "Europe/London"
    DefaultAvailability: AvailabilityWindow list
    Metadata: Map<string, string>
}
```

Availability is a repeating weekly pattern plus dated exceptions — there is no blocked-time list on the resource itself:

```fsharp
type AvailabilityWindow = {
    DayOfWeek: DayOfWeek option   // None = every day
    StartTime: TimeOnly           // local, inclusive
    EndTime: TimeOnly             // local, exclusive; <= StartTime wraps midnight
    EffectiveFrom: DateOnly option
    EffectiveTo: DateOnly option
}

and AvailabilityException = {
    Id: string
    Type: string
    Version: int
    ResourceId: ResourceId
    Date: DateOnly
    Kind: ExceptionKind
    StartTime: TimeOnly option    // required for PartialBlock / ExtendedHours
    EndTime: TimeOnly option
    Reason: string option
}

and ExceptionKind =
    | FullDay        // unavailable for the whole local day
    | PartialBlock   // unavailable during a window inside the day
    | ExtendedHours  // available OUTSIDE the default windows
```

### `TimeSlot`

A free slot produced by `FindAvailableSlots`. Slots carry no status — an occupied window simply is not emitted.

```fsharp
type TimeSlot = {
    Start: DateTimeOffset
    End: DateTimeOffset
    ResourceId: ResourceId
}

/// A half-open instant range — `[Start, End)`.
and DateRange = {
    Start: DateTimeOffset
    End: DateTimeOffset
}
```

### `Booking`

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
    BookedBy: string             // principal who created the booking
    BookedFor: string option     // when an admin books on behalf of someone
    Recurrence: RecurrenceRule option
    ParentBookingId: BookingId option
    Metadata: Map<string, string>
}

and BookingStatus =
    | Tentative   // soft reservation; still counts for conflict detection
    | Confirmed   // hard reservation
    | Cancelled   // ignored by conflict detection
    | NoShow      // retained for audit, ignored by future conflict detection
```

### `RecurrenceRule`

```fsharp
type RecurrenceRule = {
    Frequency: RecurrenceFrequency
    Interval: int                 // "every N units of Frequency"
    ByWeekday: DayOfWeek list     // filters Weekly emissions; ignored otherwise
    Until: DateTimeOffset option  // exclusive upper bound
    Count: int option             // total occurrences including the seed
}

and RecurrenceFrequency =
    | Daily
    | Weekly
    | Monthly
    | Yearly
```

Termination is `Until`, or `Count`, or neither — with neither, the caller's window plus the expander's hard cap are the only bounds.

### `BookingError` / `BookingConflict`

Schedule disagreement is one case of the error DU, carrying every reason at once so the UI can surface them together.

```fsharp
type BookingError =
    | UnknownResource of ResourceId
    | UnknownBooking of BookingId
    | Conflicts of BookingConflict list
    | InvalidRecurrence of message: string
    | InvalidWindow of message: string

and BookingConflict =
    | ResourceUnavailable of AvailabilityException
    | OverlappingBooking of BookingId
    | OutsideAvailability of window: AvailabilityWindow option
    | RecurrenceOverflow of rule: RecurrenceRule * occurrences: int
```

### `ISchedulingApi`

The ToolUp.Remoting record-of-functions. Every method is `[<AllowAnonymous>]` — the handler applies no per-method role gate, because `StorageScope` isolation is the gating layer (an honest classification of today's behaviour, not a policy choice).

```fsharp
type ISchedulingApi = {
    RegisterResource: BookableResource -> Async<Result<unit, BookingError>>
    GetResource: ResourceId -> Async<BookableResource option>
    ListResources: unit -> Async<BookableResource list>

    Book: Booking -> Async<Result<Booking, BookingError>>
    GetBooking: BookingId -> Async<Booking option>
    Cancel: BookingId * string -> Async<Result<unit, BookingError>>
    Reschedule: RescheduleRequest -> Async<Result<Booking, BookingError>>
    MarkNoShow: BookingId -> Async<Result<unit, BookingError>>
    ListBookings: ResourceId * DateRange -> Async<Booking list>

    AddAvailabilityException: AvailabilityException -> Async<Result<unit, BookingError>>
    RemoveAvailabilityException: string -> Async<Result<unit, BookingError>>
    ListAvailabilityExceptions: ResourceId * DateRange -> Async<AvailabilityException list>

    FindAvailableSlots: SlotSearchRequest -> Async<TimeSlot list>
    DetectConflicts: Booking -> Async<BookingConflict list>
    ExpandRecurrence: Booking * DateRange -> Async<Booking list>
}

and RescheduleRequest = {
    Id: BookingId
    NewStart: DateTimeOffset
    NewEnd: DateTimeOffset
}

and SlotSearchRequest = {
    ResourceId: ResourceId
    Window: DateRange
    SlotDurationMinutes: int
}
```

### `RecurrenceExpander`

```fsharp skip=signature
module RecurrenceExpander =
    /// The occurrence start instants a rule emits from a seed, up to
    /// `upperBound` and a hard cap of 10,000.
    val occurrenceStarts: seed: DateTimeOffset -> rule: RecurrenceRule -> upperBound: DateTimeOffset -> DateTimeOffset list
    /// Expand a seed booking into its occurrences inside a window.
    val expand: seed: Booking -> rule: RecurrenceRule -> window: DateRange -> Booking list
```

An unbounded rule (`Until = None`, `Count = None`) is legal — the caller's window and the hard cap are then the only bounds. A structurally invalid rule surfaces as `BookingError.InvalidRecurrence`, and a rule that blows the cap inside the requested window surfaces as `BookingConflict.RecurrenceOverflow`.

### `iCalendar`

Round-trips an RFC 5545 subset, in both directions.

```fsharp skip=signature
type VEvent = {
    Uid: string
    Summary: string
    DtStart: DateTimeOffset
    DtEnd: DateTimeOffset
    DtStamp: DateTimeOffset
    Tzid: string option
    RRule: RecurrenceRule option
    Description: string option
}

type VCalendar = {
    Version: string
    ProdId: string      // preserved on parse; CanonicalProdId on our own emissions
    Events: VEvent list
}

module iCalendar =
    val parse: raw: string -> Result<VCalendar, string>
    val emit: cal: VCalendar -> string
    val bookingToVEvent: b: Booking -> VEvent
    /// `defaults` supplies the fields iCal does not carry — ResourceId,
    /// Status, BookedBy, BookedFor, ParentBookingId, Metadata.
    val vEventToBooking: defaults: Booking -> v: VEvent -> Booking
```

`bookingToVEvent` is lossy by design: the target is interoperability with a calendar client that does not speak ToolUp, not perfect persistence round-trip.

## `ToolUp.Scheduling.Server`

### `IBookingScheduler`

Every method is scope-first and tupled, and the mutating ones take the acting user id so the emitted audit payload names an actor.

```fsharp
type IBookingScheduler =
    abstract RegisterResource: scopeId: string * resource: BookableResource -> Async<Result<unit, BookingError>>
    abstract GetResource: scopeId: string * id: ResourceId -> Async<BookableResource option>
    abstract ListResources: scopeId: string -> Async<BookableResource list>

    abstract Book: scopeId: string * booking: Booking * actorUserId: string -> Async<Result<Booking, BookingError>>
    abstract GetBooking: scopeId: string * id: BookingId -> Async<Booking option>
    abstract Cancel:
        scopeId: string * id: BookingId * reason: string * actorUserId: string -> Async<Result<unit, BookingError>>
    abstract Reschedule:
        scopeId: string * id: BookingId * newStart: DateTimeOffset * newEnd: DateTimeOffset * actorUserId: string ->
            Async<Result<Booking, BookingError>>
    abstract MarkNoShow: scopeId: string * id: BookingId * actorUserId: string -> Async<Result<unit, BookingError>>
    abstract ListBookings: scopeId: string * resourceId: ResourceId * window: DateRange -> Async<Booking list>

    abstract AddAvailabilityException: scopeId: string * exc: AvailabilityException -> Async<Result<unit, BookingError>>
    abstract RemoveAvailabilityException: scopeId: string * id: string -> Async<Result<unit, BookingError>>
    abstract ListAvailabilityExceptions:
        scopeId: string * resourceId: ResourceId * window: DateRange -> Async<AvailabilityException list>

    // Read-only utilities — no writes, no events.
    abstract FindAvailableSlots:
        scopeId: string * resourceId: ResourceId * window: DateRange * slotDurationMinutes: int -> Async<TimeSlot list>
    abstract DetectConflicts: scopeId: string * booking: Booking -> Async<BookingConflict list>
    abstract ExpandRecurrence: scopeId: string * booking: Booking * within: DateRange -> Async<Booking list>
```

Default impl: `BookingScheduler` over `IEntityStore`. Per-`ResourceId` `SemaphoreSlim` for concurrency. `Book` and `Reschedule` run `DetectConflicts` first and return `Error (Conflicts …)` without persisting; `Cancel` and `MarkNoShow` are idempotent and emit their event only on the first transition.

### `BookingConflictDetector`

```fsharp skip=signature
module BookingConflictDetector =
    val detect: inputs: DetectorInputs -> proposed: Booking -> BookingConflict list
```

Pure function — the resource, its existing bookings and its availability exceptions go in as `DetectorInputs`, the conflicts come out. Used internally by `Book` and `Reschedule`; exposed so a client can preview the same verdict before submitting.

### `SchedulingServerApp`

```fsharp skip=signature
type SchedulingServerApp = {
    Base: ServerApp
    /// Reminder fusion is opt-in and currently a no-op placeholder.
    RemindersEnabled: bool
}

module SchedulingServerApp =
    val create: unit -> SchedulingServerApp
    val run: SchedulingServerApp -> int
```

Every `ServerApp.with*` is mirrored as a delegating helper on `SchedulingServerApp`. Resources are not declared at compose time — register them at runtime through `ISchedulingApi.RegisterResource` / `IBookingScheduler.RegisterResource`, which persists into the caller's scope.

## Client tier

There is **no `ToolUp.Scheduling.Client` package** — no built-in calendar UI, no shipped proxy value, and no recurrence form components. `ToolUp.Scheduling.Client.props` exists but injects no sources today.

The shared types and `ISchedulingApi` live in `ToolUp.Scheduling.Core`, which is Fable-compilable, so a consumer builds the proxy itself with the standard ToolUp.Remoting client and `SchedulingApi.routeBuilder`:

```fsharp skip=fragment
open Elmish
open ToolUp.Remoting.Client
open ToolUp.Scheduling.SchedulingApi

let schedulingApi =
    Remoting.createApi ()
    |> Remoting.withRouteBuilder routeBuilder
    |> Remoting.buildProxy<ISchedulingApi>

Cmd.OfAsync.either schedulingApi.Book booking BookSucceeded BookFailed
```

## Events emitted to `IEventStore`

Under `SourceModule = "_scheduling"` — note *not* `_platform.scheduling`, since `_platform.*` is the core SDK's reserved namespace. Read the trail with `IEventStore.ReadBySource(scopeId, SchedulingEvents.SourceModule)`.

- `BookingCreated`
- `BookingCancelled`
- `BookingRescheduled`
- `BookingNoShow`

Each `EventType` is a `[<Literal>]` on `SchedulingEvents`, and each payload is a plain record of primitives so the default `IEventStore` serialises it with `System.Text.Json`.

## HTTP endpoints

Auto-injected by the scheduling compose step:

- `POST /api/ISchedulingApi/*` — every method on the interface, via `SchedulingApi.routeBuilder`.

## Configuration knobs

- `ServerConfig.EntityStore = EnabledEntityStore` — required; `BookableResource`, `Booking` and `AvailabilityException` are entity-store records (hence the `Type` / `Version` fields).

## Conformance test pack

`ToolUp.Scheduling.Tests` ships:
- `IBookingSchedulerContract` — the portable pack any `IBookingScheduler` implementation must clear: resource registration, booking with conflict detection, cancel / reschedule / no-show idempotence, availability exceptions, slot derivation, and the events each transition must emit.
- `RecurrenceExpanderTests`, `BookingConflictDetectorTests`, `iCalendarTests`, `WorkedExampleTests` — in-process packs over the shipped default implementation.

An external implementation binds into the contract pack by supplying a `SchedulerFactory` — the scheduler, a reader for the events it emitted, and the scope id the pack should work in:

```fsharp skip=fragment
open Expecto
open ToolUp.Scheduling.Tests.Contracts

type SchedulerFactory = unit -> IBookingScheduler * (unit -> ModuleEvent list) * string

[<Tests>]
let tests =
    IBookingSchedulerContract.tests "MyBookingScheduler" (fun () ->
        let events = ResizeArray<ModuleEvent>()
        MyBookingScheduler.create events :> IBookingScheduler, (fun () -> List.ofSeq events), "scope-1")
```

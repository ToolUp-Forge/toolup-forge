module ToolUp.Scheduling.SchedulingApi

open System
open ToolUp.Scheduling.SchedulingTypes

// ─── Phase 20 — Fable.Remoting wire contract ────────────────────────
//
// The client-facing API. Method shapes mirror `IBookingScheduler` minus
// the per-request `scopeId` / actor parameters — those are resolved
// server-side from the caller's `AccessContext`. Writes that mutate
// shared state are Owner/Admin-gated by the handler before reaching
// `IBookingScheduler`.
//
// Read methods return data directly; write methods return
// `Result<X, BookingError>` so the UI can branch on conflict /
// not-found / invalid-shape / storage-failure cases. Conflicts ride
// `BookingError.Conflicts` (added to the DU for this layer); other
// failure modes ride their dedicated cases.

/// Reschedule request payload — a tagged record so Fable.Remoting
/// serialises the three positional arguments as a single named tuple
/// on the wire.
type RescheduleRequest = {
    Id: BookingId
    NewStart: DateTimeOffset
    NewEnd: DateTimeOffset
}

/// `ListAvailableSlots` request payload — `Window` is the candidate
/// window, `SlotDurationMinutes` is the requested slot length.
type SlotSearchRequest = {
    ResourceId: ResourceId
    Window: DateRange
    SlotDurationMinutes: int
}

/// Fable.Remoting record-of-functions. Each call goes over HTTP via
/// `Fable.Remoting.Client` proxy; server-side handler in
/// `Server/SchedulingApiHandler.fs` resolves the AccessContext,
/// applies write gating, and delegates to `IBookingScheduler`.
type ISchedulingApi = {
    // ─── Resources ───────────────────────────────────────────────
    RegisterResource: BookableResource -> Async<Result<unit, BookingError>>
    GetResource: ResourceId -> Async<BookableResource option>
    ListResources: unit -> Async<BookableResource list>

    // ─── Bookings ────────────────────────────────────────────────
    Book: Booking -> Async<Result<Booking, BookingError>>
    GetBooking: BookingId -> Async<Booking option>
    Cancel: BookingId * string -> Async<Result<unit, BookingError>>
    Reschedule: RescheduleRequest -> Async<Result<Booking, BookingError>>
    MarkNoShow: BookingId -> Async<Result<unit, BookingError>>
    ListBookings: ResourceId * DateRange -> Async<Booking list>

    // ─── Availability ────────────────────────────────────────────
    AddAvailabilityException: AvailabilityException -> Async<Result<unit, BookingError>>
    RemoveAvailabilityException: string -> Async<Result<unit, BookingError>>
    ListAvailabilityExceptions: ResourceId * DateRange -> Async<AvailabilityException list>

    // ─── Read-only utilities ─────────────────────────────────────
    FindAvailableSlots: SlotSearchRequest -> Async<TimeSlot list>
    DetectConflicts: Booking -> Async<BookingConflict list>
    ExpandRecurrence: Booking * DateRange -> Async<Booking list>
}

/// HTTP route prefix for the scheduling API. Mirrors
/// `IPlatformApi.routeBuilder` convention so the client proxy and
/// the server handler agree on URL shape without naming the routes
/// individually.
let routeBuilder (typeName: string) (methodName: string) : string =
    sprintf "/api/%s/%s" typeName methodName
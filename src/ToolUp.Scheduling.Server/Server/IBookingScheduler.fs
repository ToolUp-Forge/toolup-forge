module ToolUp.Scheduling.IBookingScheduler

open System
open ToolUp.Scheduling.SchedulingTypes

// ─── Phase 20 — IBookingScheduler interface ─────────────────────────
//
// Server-side abstraction over booking, conflict detection, and
// availability lookup. Default implementation
// (`ToolUp.Scheduling.BookingScheduler`) wraps `IEntityStore`
// (Phase 19) for persistence and writes lifecycle events to
// `IEventStore` under `SourceModule = "_scheduling"` for audit.
//
// **Scope isolation by construction.** Every method takes
// `scopeId: string` as the first parameter; implementations derive
// container paths from `scopeId` so cross-scope reads / writes are
// structurally impossible. Same discipline as `IDataObjectStore` /
// `IEntityStore` / `IBlobStorage`.
//
// **Six-rule portability audit (GP-12 / Phase 9c):**
//
//   1. Identity by value — `ResourceId` / `BookingId` are `string`,
//      `scopeId: string`, no live handles. A distributed companion
//      (e.g., Akka.NET / Orleans) returns the same primitives.
//
//   2. Async at every boundary — every method returns `Async<_>`.
//      No fire-and-forget `Tell` shapes; no synchronous
//      `unit -> Booking list` reads.
//
//   3. Retry / supervision as data — failure surfaces through
//      `BookingError` (typed DU) and `BookingConflict` (typed DU),
//      not through callbacks or supervision-strategy objects.
//      Retries are caller-side; the interface is policy-free.
//
//   4. Stateless between calls — no method assumes in-memory state
//      survives between invocations. The default impl caches
//      nothing; a grain that deactivates and re-activates between
//      `Book` and `DetectConflicts` behaves identically. State
//      lives in `IEntityStore` (Phase 19).
//
//   5. No cross-shard ordering — write operations are serialised
//      per-`ResourceId` (the implementation may use a per-key lock,
//      a single grain per resource, or actor-based serialisation).
//      Across resources no ordering is promised. Across scopes is
//      similarly free.
//
//   6. Precision at lower bound — datetime parameters are
//      `DateTimeOffset` (BCL precision: 100 ns ticks). Booking
//      semantics are minute-grain; sub-minute precision is
//      preserved on save but conflict detection rounds at the
//      minute boundary. Documented in TECHNICAL_GUIDE.

/// The default implementation of `IBookingScheduler` ships in
/// `BookingScheduler.fs`. Custom implementations (a Hangfire-backed
/// dispatcher, an Orleans grain layer, an in-memory test stub)
/// implement this interface and are wired via
/// `services.AddSingleton<IBookingScheduler>(...)`.
type IBookingScheduler =

    // ─── Resources (writes Owner/Admin-gated at the handler) ─────

    /// Register or update a `BookableResource`. The store assigns
    /// `Version` on save; existing records are overwritten with the
    /// new version. Returns `Error InvalidWindow` if any
    /// `DefaultAvailability` window is malformed
    /// (`Start = End` for non-wrapping; bad timezone string).
    abstract RegisterResource: scopeId: string * resource: BookableResource -> Async<Result<unit, BookingError>>

    /// Fetch a resource by id. `None` when no resource with that id
    /// exists in the scope.
    abstract GetResource: scopeId: string * id: ResourceId -> Async<BookableResource option>

    /// Page through every resource in the scope. Order is by
    /// `ResourceId` ascending — deterministic across cold starts.
    abstract ListResources: scopeId: string -> Async<BookableResource list>

    // ─── Bookings ────────────────────────────────────────────────

    /// Create a new booking. Runs `DetectConflicts` first; if any
    /// conflicts exist, returns `Error (Conflicts ...)` without
    /// persisting. On success emits `BookingCreated` to
    /// `IEventStore` under `SourceModule = "_scheduling"`.
    /// `actorUserId` is recorded in the audit payload.
    abstract Book: scopeId: string * booking: Booking * actorUserId: string -> Async<Result<Booking, BookingError>>

    /// Fetch a booking by id. `None` when not found in the scope.
    abstract GetBooking: scopeId: string * id: BookingId -> Async<Booking option>

    /// Mark a booking as `Cancelled`. Idempotent — cancelling an
    /// already-cancelled booking is a successful no-op. Emits
    /// `BookingCancelled` on the first transition.
    abstract Cancel:
        scopeId: string * id: BookingId * reason: string * actorUserId: string -> Async<Result<unit, BookingError>>

    /// Move an existing booking to a new window. Runs
    /// `DetectConflicts` against the new window before persisting;
    /// on conflict returns `Error (Conflicts ...)`. Emits
    /// `BookingRescheduled` on success.
    abstract Reschedule:
        scopeId: string * id: BookingId * newStart: DateTimeOffset * newEnd: DateTimeOffset * actorUserId: string ->
            Async<Result<Booking, BookingError>>

    /// Mark a confirmed booking as `NoShow` after the fact. Idempotent.
    /// Emits `BookingNoShow` on the first transition. Tentative or
    /// already-cancelled bookings cannot transition to NoShow.
    abstract MarkNoShow: scopeId: string * id: BookingId * actorUserId: string -> Async<Result<unit, BookingError>>

    /// List bookings on a resource within a half-open window.
    /// Includes any booking whose `[StartUtc, EndUtc)` intersects
    /// `[window.Start, window.End)`. Returns Cancelled and NoShow
    /// bookings too — callers filter by `Status` if they only want
    /// active reservations.
    abstract ListBookings: scopeId: string * resourceId: ResourceId * window: DateRange -> Async<Booking list>

    // ─── Availability exceptions ─────────────────────────────────

    /// Add a one-off availability override. Validates that
    /// `PartialBlock` and `ExtendedHours` carry both `StartTime`
    /// and `EndTime`. `FullDay` exceptions ignore the times.
    abstract AddAvailabilityException: scopeId: string * exc: AvailabilityException -> Async<Result<unit, BookingError>>

    /// Remove an availability exception by id. Idempotent —
    /// removing a non-existent id returns `Ok` without error.
    abstract RemoveAvailabilityException: scopeId: string * id: string -> Async<Result<unit, BookingError>>

    /// List availability exceptions for a resource within a window.
    /// Filtered by `Date ∈ [window.Start.Date, window.End.Date)` —
    /// callers wanting all exceptions pass a wide window.
    abstract ListAvailabilityExceptions:
        scopeId: string * resourceId: ResourceId * window: DateRange -> Async<AvailabilityException list>

    // ─── Read-only utilities (no writes, no events) ──────────────

    /// Compute conflict-free time slots of a fixed duration on a
    /// resource within a window. Walks the resource's default
    /// availability windows, subtracts existing bookings (excluding
    /// Cancelled / NoShow), subtracts FullDay / PartialBlock
    /// exceptions, adds ExtendedHours exceptions; emits slots of
    /// `slotDurationMinutes` on the boundaries that survive.
    abstract FindAvailableSlots:
        scopeId: string * resourceId: ResourceId * window: DateRange * slotDurationMinutes: int -> Async<TimeSlot list>

    /// Detect conflicts for a *proposed* (or existing) booking.
    /// Pure read — no side effects. Returns the empty list when
    /// the booking would succeed.
    abstract DetectConflicts: scopeId: string * booking: Booking -> Async<BookingConflict list>

    /// Expand a booking's `Recurrence` rule into concrete
    /// occurrences within `within`. Delegates to
    /// `RecurrenceExpander.expand` — this method exists on the
    /// interface so distributed companions can offload the
    /// expansion to the same node that owns the resource state.
    abstract ExpandRecurrence: scopeId: string * booking: Booking * within: DateRange -> Async<Booking list>
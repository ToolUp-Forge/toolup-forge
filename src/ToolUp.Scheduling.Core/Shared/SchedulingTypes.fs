module ToolUp.Scheduling.SchedulingTypes

open System

// ─── Phase 20 — Scheduling companion shared types ───────────────────
//
// Domain-neutral booking primitives. These records describe bookable
// resources, bookings, availability windows, and recurrence rules
// without naming any specific domain (rooms / equipment / people /
// slots are all the same shape — only `BookableResource.ResourceType`
// distinguishes them).
//
// The three entity records (`BookableResource`, `Booking`,
// `AvailabilityException`) carry the `Id` / `Type` / `Version`
// fields required by Phase 19's `IEntityStore` reflection contract.
// `Type` distinguishes the entity kind at the storage layer
// (`"BookableResource"` / `"Booking"` / `"AvailabilityException"`);
// `Version` is overwritten by the store on every save.
//
// Intentionally Platform-decoupled: this file does NOT open
// `ToolUp.Platform.EntityTypes`. The entity store only needs the
// field names + BCL types to be present at reflection time, so a
// plain `Id: string` satisfies the contract without dragging the
// Platform's reflection helpers into Fable.
//
// All types are Fable-compatible: `DateOnly` / `TimeOnly` /
// `DateTimeOffset` / `DayOfWeek` are net6+ BCL types that modern
// Fable supports. If a future Fable check surfaces a problem we
// fall back to richer representations on the Fable side.

/// Stable identifier for a bookable resource. Type alias for
/// `string` so natural-key domains (employee numbers, room codes)
/// flow through unchanged.
type ResourceId = string

/// Stable identifier for a booking. Type alias for `string`.
type BookingId = string

/// Recurrence frequency. v1 covers the four common cases — sub-day
/// (Hourly / Minutely) and complex monthly rules (BySetPos /
/// ByMonthDay) deferred to Phase 20 follow-ups if needed.
type RecurrenceFrequency =
    | Daily
    | Weekly
    | Monthly
    | Yearly

/// One recurrence rule. `Interval = N` means "every N units of the
/// chosen frequency". `ByWeekday` filters Weekly emissions to the
/// listed days; ignored for non-Weekly frequencies in v1.
/// Termination: bounded by `Until` (exclusive upper bound) OR
/// `Count` (total occurrence count including the seed) OR neither
/// (caller's `window` is the only bound — combined with the hard cap
/// in `RecurrenceExpander`).
type RecurrenceRule = {
    Frequency: RecurrenceFrequency
    Interval: int
    ByWeekday: DayOfWeek list
    Until: DateTimeOffset option
    Count: int option
}

/// One availability window for a resource. Times are local to the
/// resource's timezone; the conflict detector lifts them to UTC
/// against `BookableResource.Timezone` at evaluation time.
type AvailabilityWindow = {
    /// `None` = window applies on every day. `Some d` = window
    /// applies only on day-of-week `d`.
    DayOfWeek: DayOfWeek option
    /// Local start time, inclusive.
    StartTime: TimeOnly
    /// Local end time, exclusive. `EndTime <= StartTime` means a
    /// window that wraps midnight (e.g., night-shift availability);
    /// the conflict detector handles this case explicitly.
    EndTime: TimeOnly
    /// Optional inclusive lower bound on dates this window applies
    /// to. `None` = applies indefinitely back.
    EffectiveFrom: DateOnly option
    /// Optional inclusive upper bound on dates this window applies
    /// to. `None` = applies indefinitely forward.
    EffectiveTo: DateOnly option
}

/// What an availability exception does on its date.
type ExceptionKind =
    /// Resource is unavailable for the whole local day. `StartTime`
    /// / `EndTime` are ignored.
    | FullDay
    /// Resource is unavailable during a window inside the day.
    /// Requires `StartTime` and `EndTime` to be `Some _`.
    | PartialBlock
    /// Resource is available *outside* its default windows.
    /// Requires `StartTime` and `EndTime` to be `Some _`.
    | ExtendedHours

/// Where a booking is in its lifecycle.
type BookingStatus =
    /// Pending confirmation; counts as a soft reservation for
    /// conflict detection.
    | Tentative
    /// Active booking; counts as a hard reservation.
    | Confirmed
    /// Cancelled; ignored by conflict detection.
    | Cancelled
    /// Confirmed booking that the booker did not attend; retained
    /// for audit but ignored by future conflict detection.
    | NoShow

/// A bookable resource. `ResourceType` is the user-facing
/// classification (`"Person"` / `"Room"` / `"Equipment"` / any
/// caller-defined string); the entity-store discriminator is the
/// constant `Type` field.
type BookableResource = {
    Id: ResourceId
    Type: string
    Version: int
    ResourceType: string
    DisplayName: string
    /// IANA timezone identifier, e.g. `"Europe/London"` /
    /// `"America/New_York"`. Used by the conflict detector and the
    /// availability calendar view to render times correctly.
    Timezone: string
    DefaultAvailability: AvailabilityWindow list
    Metadata: Map<string, string>
}

/// A one-off override of a resource's default availability for a
/// specific local date.
type AvailabilityException = {
    Id: string
    Type: string
    Version: int
    ResourceId: ResourceId
    Date: DateOnly
    Kind: ExceptionKind
    StartTime: TimeOnly option
    EndTime: TimeOnly option
    Reason: string option
}

/// A scheduled booking. `StartUtc` / `EndUtc` are stored as UTC;
/// rendering against the resource's local timezone happens at
/// display time. `Recurrence = Some _` marks this as the seed of a
/// recurring series; `ParentBookingId = Some _` marks an occurrence
/// override of an existing series.
type Booking = {
    Id: BookingId
    Type: string
    Version: int
    ResourceId: ResourceId
    Title: string
    StartUtc: DateTimeOffset
    EndUtc: DateTimeOffset
    Status: BookingStatus
    /// User identifier of the principal who created the booking.
    BookedBy: string
    /// Optional separate "for whom" identity — useful when an
    /// administrator books on behalf of another user.
    BookedFor: string option
    Recurrence: RecurrenceRule option
    ParentBookingId: BookingId option
    Metadata: Map<string, string>
}

/// A half-open instant range. `End` is exclusive — `[Start, End)`.
type DateRange = {
    Start: DateTimeOffset
    End: DateTimeOffset
}

/// A free time slot on a resource — produced by `FindAvailableSlots`
/// and consumed by the calendar view.
type TimeSlot = {
    Start: DateTimeOffset
    End: DateTimeOffset
    ResourceId: ResourceId
}

/// Why a proposed booking conflicts with the current schedule.
type BookingConflict =
    /// The booking falls on (or overlaps) an availability exception
    /// that blocks the resource (`FullDay` or `PartialBlock`).
    | ResourceUnavailable of AvailabilityException
    /// The booking overlaps an existing Tentative or Confirmed
    /// booking on the same resource.
    | OverlappingBooking of BookingId
    /// The booking falls outside any of the resource's default
    /// availability windows for the relevant day. `Window` carries
    /// the *closest* window for diagnostic display, or `None` if
    /// the resource has no availability defined for that day.
    | OutsideAvailability of window: AvailabilityWindow option
    /// A recurring rule expanded to more than the hard cap of
    /// occurrences inside the requested window. Carries the rule
    /// and the count emitted before the cap kicked in.
    | RecurrenceOverflow of rule: RecurrenceRule * occurrences: int

/// Why a scheduling operation failed. `Conflicts` carries the
/// schedule-disagreement case — distinct from request-shape /
/// lookup / storage failures so callers can branch on it cleanly.
type BookingError =
    /// Lookup hit no resource with the supplied id in the caller's
    /// scope.
    | UnknownResource of ResourceId
    /// Lookup hit no booking with the supplied id in the caller's
    /// scope.
    | UnknownBooking of BookingId
    /// The proposed booking conflicts with the resource's
    /// availability windows, exceptions, or existing bookings.
    /// Carries the full list of conflicts so the UI can surface
    /// every reason at once.
    | Conflicts of BookingConflict list
    /// The recurrence rule was structurally invalid (e.g.,
    /// `Interval <= 0`, both `Until` and `Count` set to terminating
    /// values that disagree, weekday list referencing values
    /// outside `DayOfWeek`).
    | InvalidRecurrence of message: string
    /// A `DateRange` had `Start >= End`, or a `TimeOnly` window had
    /// `Start = End` for a non-wrapping window.
    | InvalidWindow of message: string
    /// Underlying entity store / event store / job scheduler
    /// returned an error that couldn't be mapped to a more specific
    /// case.
    | StorageFailure of message: string
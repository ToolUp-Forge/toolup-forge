module ToolUp.Scheduling.BookingConflictDetector

open System
open ToolUp.Scheduling.SchedulingTypes

// ─── Phase 20 — Booking conflict detection ───────────────────────
//
// Pure function. The default `IBookingScheduler` impl fetches the
// resource record + relevant existing bookings + relevant
// availability exceptions from `IEntityStore` and then asks this
// module for the verdict. Tests can exercise the detector directly
// with literal inputs, no DI.
//
// Conflict checks (in order):
//   1. **Overlapping bookings.** Any existing `Tentative` /
//      `Confirmed` booking on the same resource whose UTC range
//      intersects the proposed booking's range. Bookings with
//      `Status = Cancelled | NoShow` are ignored. The proposed
//      booking's own `Id` is excluded from the check so reschedule
//      doesn't conflict with itself.
//   2. **Availability exceptions** on the booking's local date:
//      `FullDay` → always a `ResourceUnavailable`; `PartialBlock`
//      whose local-time window overlaps the booking's local-time
//      window → `ResourceUnavailable`; `ExtendedHours` adds
//      availability and never produces a conflict by itself.
//   3. **Default availability + ExtendedHours.** The booking's
//      local-time window must fit fully inside at least one
//      window — either a default `AvailabilityWindow` for that
//      day-of-week (filtered by `EffectiveFrom`/`EffectiveTo`) or
//      an `ExtendedHours` exception. Otherwise:
//      `OutsideAvailability`. The closest default window for the
//      day is attached for diagnostic display.
//
// **Simplifications** (v1 — see TECHNICAL_GUIDE for the deferred set):
//   * Multi-day bookings: only the start date's local window is
//     checked. Bookings that span midnight are not v1.
//   * Wrapping windows (`EndTime <= StartTime` — night shifts): the
//     detector treats them as "fits any time on this day". Caller
//     gets no false-positive conflicts but gets no enforcement on
//     true mid-night-gap bookings either.
//   * Bad TZID on the resource: detector falls back to UTC. No
//     exception is raised; mismatch surfaces as benign drift on
//     the local-time check.

/// Inputs to the pure detector. Caller pre-filters bookings and
/// exceptions to the relevant resource and date range, so the
/// detector doesn't pay an O(N) full-scan cost.
type DetectorInputs = {
    Resource: BookableResource
    /// Existing bookings on `Resource.Id` whose `[StartUtc, EndUtc)`
    /// intersects the proposed booking's window.
    Bookings: Booking list
    /// Availability exceptions on `Resource.Id` whose `Date` matches
    /// the proposed booking's local date.
    Exceptions: AvailabilityException list
}

let private overlapsUtc (a: Booking) (b: Booking) : bool =
    a.StartUtc < b.EndUtc && a.EndUtc > b.StartUtc

let private resolveTimezone (zoneId: string) : TimeZoneInfo option =
    try
        Some(TimeZoneInfo.FindSystemTimeZoneById(zoneId))
    with _ ->
        None

let private toLocal (zone: TimeZoneInfo option) (instant: DateTimeOffset) : DateTime =
    match zone with
    | Some tz -> TimeZoneInfo.ConvertTimeFromUtc(instant.UtcDateTime, tz)
    | None -> instant.UtcDateTime

let private dayOfWeekMatches (windowDow: DayOfWeek option) (actual: DayOfWeek) : bool =
    match windowDow with
    | None -> true
    | Some d -> d = actual

let private withinEffectiveDates (window: AvailabilityWindow) (date: DateOnly) : bool =
    let aboveLower =
        match window.EffectiveFrom with
        | None -> true
        | Some f -> f <= date

    let belowUpper =
        match window.EffectiveTo with
        | None -> true
        | Some t -> date <= t

    aboveLower && belowUpper

let private fitsInWindow (window: AvailabilityWindow) (start: TimeOnly) (endT: TimeOnly) : bool =
    if window.EndTime <= window.StartTime then
        // Wrapping (night-shift) — v1 treats as always-available on
        // this day. TECHNICAL_GUIDE flags this for follow-up.
        true
    else
        start >= window.StartTime && endT <= window.EndTime

/// Detect conflicts for a proposed booking. Pure. Returns the empty
/// list when the booking would succeed; otherwise returns every
/// reason in the order described in the file header.
let detect (inputs: DetectorInputs) (proposed: Booking) : BookingConflict list =
    let conflicts = ResizeArray<BookingConflict>()

    // 1. Overlapping bookings.
    for existing in inputs.Bookings do
        let isSelf = existing.Id = proposed.Id

        let isActive =
            match existing.Status with
            | Tentative
            | Confirmed -> true
            | Cancelled
            | NoShow -> false

        if not isSelf && isActive && overlapsUtc existing proposed then
            conflicts.Add(OverlappingBooking existing.Id)

    // 2 + 3. Local time checks.
    let tz = resolveTimezone inputs.Resource.Timezone
    let localStart = toLocal tz proposed.StartUtc
    let localEnd = toLocal tz proposed.EndUtc
    let localDate = DateOnly.FromDateTime localStart
    let localDow = localStart.DayOfWeek
    let bookingStartTime = TimeOnly.FromDateTime localStart
    let bookingEndTime = TimeOnly.FromDateTime localEnd

    // 2. Exceptions on the local date.
    for exc in inputs.Exceptions do
        if exc.Date = localDate then
            match exc.Kind with
            | FullDay -> conflicts.Add(ResourceUnavailable exc)
            | PartialBlock ->
                match exc.StartTime, exc.EndTime with
                | Some es, Some ee when es < bookingEndTime && ee > bookingStartTime ->
                    conflicts.Add(ResourceUnavailable exc)
                | _ -> ()
            | ExtendedHours -> ()

    // 3. Default availability + ExtendedHours.
    let dayWindows =
        inputs.Resource.DefaultAvailability
        |> List.filter (fun w -> dayOfWeekMatches w.DayOfWeek localDow && withinEffectiveDates w localDate)

    let extendedWindows =
        inputs.Exceptions
        |> List.filter (fun e -> e.Date = localDate && e.Kind = ExtendedHours)
        |> List.choose (fun e ->
            match e.StartTime, e.EndTime with
            | Some s, Some t ->
                Some {
                    DayOfWeek = Some localDow
                    StartTime = s
                    EndTime = t
                    EffectiveFrom = None
                    EffectiveTo = None
                }
            | _ -> None)

    let allWindows = dayWindows @ extendedWindows

    let fits =
        allWindows
        |> List.exists (fun w -> fitsInWindow w bookingStartTime bookingEndTime)

    if not fits then
        conflicts.Add(OutsideAvailability(List.tryHead dayWindows))

    List.ofSeq conflicts
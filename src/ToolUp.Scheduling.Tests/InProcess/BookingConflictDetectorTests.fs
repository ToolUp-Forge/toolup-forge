module ToolUp.Scheduling.Tests.InProcess.BookingConflictDetectorTests

open System
open Expecto
open ToolUp.Scheduling.SchedulingTypes
open ToolUp.Scheduling.BookingConflictDetector

// ─── BookingConflictDetector — pure unit tests ────────────────────
//
// Detector is pure; tests build literal `DetectorInputs` and assert
// the conflict list matches expectations.

let private utc (y: int) (mo: int) (d: int) (h: int) (mi: int) : DateTimeOffset =
    DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero)

let private timeOf (h: int) (mi: int) : TimeOnly = TimeOnly(h, mi)

/// A resource available 09:00–17:00 every day in UTC.
let private alwaysAvailableResource: BookableResource = {
    Id = "R1"
    Type = "BookableResource"
    Version = 1
    ResourceType = "Person"
    DisplayName = "Alice"
    Timezone = "UTC"
    DefaultAvailability = [
        {
            DayOfWeek = None
            StartTime = timeOf 9 0
            EndTime = timeOf 17 0
            EffectiveFrom = None
            EffectiveTo = None
        }
    ]
    Metadata = Map.empty
}

let private booking (id: string) (start: DateTimeOffset) (durationHours: float) (status: BookingStatus) : Booking = {
    Id = id
    Type = "Booking"
    Version = 1
    ResourceId = "R1"
    Title = "test"
    StartUtc = start
    EndUtc = start.AddHours(durationHours)
    Status = status
    BookedBy = "u1"
    BookedFor = None
    Recurrence = None
    ParentBookingId = None
    Metadata = Map.empty
}

let private mondayMorning = utc 2026 6 1 10 0 // Mon 2026-06-01 10:00 UTC

let tests =
    testList "BookingConflictDetector" [

        test "no conflicts on a clear schedule within availability" {
            let inputs = {
                Resource = alwaysAvailableResource
                Bookings = []
                Exceptions = []
            }

            let proposed = booking "new" mondayMorning 1.0 Confirmed
            Expect.isEmpty (detect inputs proposed) "clear schedule"
        }

        test "OverlappingBooking on intersecting Confirmed booking" {
            let existing = booking "existing" mondayMorning 1.0 Confirmed

            let inputs = {
                Resource = alwaysAvailableResource
                Bookings = [ existing ]
                Exceptions = []
            }

            let proposed = booking "new" (mondayMorning.AddMinutes(30.0)) 1.0 Confirmed
            let conflicts = detect inputs proposed
            Expect.contains conflicts (OverlappingBooking "existing") "overlap detected"
        }

        test "Cancelled bookings are ignored" {
            let cancelled = booking "cancelled" mondayMorning 1.0 Cancelled

            let inputs = {
                Resource = alwaysAvailableResource
                Bookings = [ cancelled ]
                Exceptions = []
            }

            let proposed = booking "new" mondayMorning 1.0 Confirmed
            Expect.isEmpty (detect inputs proposed) "cancelled is invisible"
        }

        test "NoShow bookings are ignored" {
            let noShow = booking "noshow" mondayMorning 1.0 NoShow

            let inputs = {
                Resource = alwaysAvailableResource
                Bookings = [ noShow ]
                Exceptions = []
            }

            let proposed = booking "new" mondayMorning 1.0 Confirmed
            Expect.isEmpty (detect inputs proposed) "noshow is invisible"
        }

        test "self-id is excluded from overlap check" {
            let same = booking "same" mondayMorning 1.0 Confirmed

            let inputs = {
                Resource = alwaysAvailableResource
                Bookings = [ same ]
                Exceptions = []
            }

            // Same Id — reschedule scenario: a booking shouldn't conflict with itself.
            let proposed = booking "same" mondayMorning 1.0 Confirmed
            Expect.isEmpty (detect inputs proposed) "self excluded"
        }

        test "FullDay exception emits ResourceUnavailable" {
            let exc: AvailabilityException = {
                Id = "ex1"
                Type = "AvailabilityException"
                Version = 1
                ResourceId = "R1"
                Date = DateOnly(2026, 6, 1)
                Kind = FullDay
                StartTime = None
                EndTime = None
                Reason = Some "Holiday"
            }

            let inputs = {
                Resource = alwaysAvailableResource
                Bookings = []
                Exceptions = [ exc ]
            }

            let proposed = booking "new" mondayMorning 1.0 Confirmed
            let conflicts = detect inputs proposed
            Expect.contains conflicts (ResourceUnavailable exc) "FullDay flagged"
        }

        test "PartialBlock overlapping the booking emits conflict" {
            let exc: AvailabilityException = {
                Id = "ex1"
                Type = "AvailabilityException"
                Version = 1
                ResourceId = "R1"
                Date = DateOnly(2026, 6, 1)
                Kind = PartialBlock
                StartTime = Some(timeOf 9 30)
                EndTime = Some(timeOf 11 0)
                Reason = Some "Maintenance"
            }

            let inputs = {
                Resource = alwaysAvailableResource
                Bookings = []
                Exceptions = [ exc ]
            }

            let proposed = booking "new" mondayMorning 1.0 Confirmed // 10:00–11:00 — overlaps
            let conflicts = detect inputs proposed
            Expect.contains conflicts (ResourceUnavailable exc) "PartialBlock overlap flagged"
        }

        test "PartialBlock outside the booking does not conflict" {
            let exc: AvailabilityException = {
                Id = "ex1"
                Type = "AvailabilityException"
                Version = 1
                ResourceId = "R1"
                Date = DateOnly(2026, 6, 1)
                Kind = PartialBlock
                StartTime = Some(timeOf 13 0)
                EndTime = Some(timeOf 15 0)
                Reason = None
            }

            let inputs = {
                Resource = alwaysAvailableResource
                Bookings = []
                Exceptions = [ exc ]
            }

            let proposed = booking "new" mondayMorning 1.0 Confirmed // 10:00–11:00, doesn't overlap 13:00–15:00
            Expect.isEmpty (detect inputs proposed) "no overlap"
        }

        test "ExtendedHours adds availability outside default window" {
            // Resource available 09:00–17:00; booking proposed at 18:00.
            let exc: AvailabilityException = {
                Id = "ex1"
                Type = "AvailabilityException"
                Version = 1
                ResourceId = "R1"
                Date = DateOnly(2026, 6, 1)
                Kind = ExtendedHours
                StartTime = Some(timeOf 17 0)
                EndTime = Some(timeOf 20 0)
                Reason = Some "Late session"
            }

            let inputs = {
                Resource = alwaysAvailableResource
                Bookings = []
                Exceptions = [ exc ]
            }

            let proposed = booking "new" (utc 2026 6 1 18 0) 1.0 Confirmed
            Expect.isEmpty (detect inputs proposed) "ExtendedHours covers the booking"
        }

        test "OutsideAvailability when no window fits" {
            let inputs = {
                Resource = alwaysAvailableResource
                Bookings = []
                Exceptions = []
            }

            // Proposed at 18:00 — outside the 09:00–17:00 default window.
            let proposed = booking "new" (utc 2026 6 1 18 0) 1.0 Confirmed
            let conflicts = detect inputs proposed

            let outside =
                conflicts
                |> List.exists (fun c ->
                    match c with
                    | OutsideAvailability _ -> true
                    | _ -> false)

            Expect.isTrue outside "OutsideAvailability surfaced"
        }

        test "DayOfWeek-restricted availability rejects other days" {
            let mondayOnly: BookableResource = {
                alwaysAvailableResource with
                    DefaultAvailability = [
                        {
                            DayOfWeek = Some DayOfWeek.Monday
                            StartTime = timeOf 9 0
                            EndTime = timeOf 17 0
                            EffectiveFrom = None
                            EffectiveTo = None
                        }
                    ]
            }

            let inputs = {
                Resource = mondayOnly
                Bookings = []
                Exceptions = []
            }

            let tuesdayMorning = utc 2026 6 2 10 0 // Tuesday
            let proposed = booking "new" tuesdayMorning 1.0 Confirmed
            let conflicts = detect inputs proposed

            let outside =
                conflicts
                |> List.exists (fun c ->
                    match c with
                    | OutsideAvailability _ -> true
                    | _ -> false)

            Expect.isTrue outside "Tuesday rejected"
        }

        test "EffectiveFrom/EffectiveTo bounds availability" {
            let timeBound: BookableResource = {
                alwaysAvailableResource with
                    DefaultAvailability = [
                        {
                            DayOfWeek = None
                            StartTime = timeOf 9 0
                            EndTime = timeOf 17 0
                            EffectiveFrom = Some(DateOnly(2026, 1, 1))
                            EffectiveTo = Some(DateOnly(2026, 5, 31))
                        }
                    ]
            }

            let inputs = {
                Resource = timeBound
                Bookings = []
                Exceptions = []
            }

            // June 1 is past the EffectiveTo (May 31).
            let proposed = booking "new" mondayMorning 1.0 Confirmed
            let conflicts = detect inputs proposed

            let outside =
                conflicts
                |> List.exists (fun c ->
                    match c with
                    | OutsideAvailability _ -> true
                    | _ -> false)

            Expect.isTrue outside "expired window rejected"
        }

        test "bad timezone falls back to UTC without exception" {
            let badTz: BookableResource = {
                alwaysAvailableResource with
                    Timezone = "Not/A/Real/Zone"
            }

            let inputs = {
                Resource = badTz
                Bookings = []
                Exceptions = []
            }

            let proposed = booking "new" mondayMorning 1.0 Confirmed
            // Should not throw; UTC fallback means the 10:00 UTC booking
            // fits the 09:00–17:00 default window.
            Expect.isEmpty (detect inputs proposed) "graceful fallback"
        }
    ]